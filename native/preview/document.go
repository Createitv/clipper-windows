// Package preview extracts local, inert document text. It never executes macros,
// follows external references, or uploads content to a conversion service.
package preview

import (
	"archive/zip"
	"bytes"
	"encoding/binary"
	"encoding/xml"
	"errors"
	"fmt"
	"golang.org/x/text/encoding/simplifiedchinese"
	"io"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"unicode/utf16"
	"unicode/utf8"
)

type Result struct {
	Kind string `json:"kind"`
	Text string `json:"text,omitempty"`
}

const maxExpanded = 32 * 1024 * 1024
const maxText = 16 * 1024 * 1024

func Document(name string, data []byte) (Result, error) {
	ext := strings.ToLower(filepath.Ext(name))
	switch ext {
	case ".pdf":
		return Result{Kind: "pdf"}, nil
	case ".rtf":
		return Result{Kind: "rtf"}, nil
	case ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".ico", ".heic":
		return Result{Kind: "image"}, nil
	case ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".mp4", ".mov", ".m4v", ".webm", ".avi":
		return Result{Kind: "media"}, nil
	case ".docx", ".docm", ".xlsx", ".xlsm", ".pptx", ".pptm", ".odt", ".ods", ".odp", ".epub", ".zip":
		s, e := archiveText(ext, data)
		return Result{Kind: "document", Text: s}, e
	case ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".jsonl", ".xml", ".html", ".htm", ".yaml", ".yml", ".toml", ".ini", ".log", ".sql", ".go", ".py", ".js", ".jsx", ".ts", ".tsx", ".css", ".swift", ".c", ".h", ".cpp", ".cs", ".java", ".kt", ".sh", ".ps1", ".rs", ".vue", ".svelte", ".svg":
		s, e := decodeText(data)
		return Result{Kind: "text", Text: s}, e
	default:
		return Result{Kind: "unsupported"}, nil
	}
}
func decodeText(data []byte) (string, error) {
	if len(data) > maxText {
		return "", errors.New("document text exceeds 16 MiB preview limit")
	}
	if bytes.HasPrefix(data, []byte{0xff, 0xfe}) || bytes.HasPrefix(data, []byte{0xfe, 0xff}) {
		little := data[0] == 0xff
		data = data[2:]
		if len(data)%2 != 0 {
			return "", errors.New("invalid UTF-16 document")
		}
		words := make([]uint16, len(data)/2)
		for i := range words {
			if little {
				words[i] = binary.LittleEndian.Uint16(data[i*2:])
			} else {
				words[i] = binary.BigEndian.Uint16(data[i*2:])
			}
		}
		return string(utf16.Decode(words)), nil
	}
	data = bytes.TrimPrefix(data, []byte{0xef, 0xbb, 0xbf})
	if bytes.IndexByte(data, 0) >= 0 {
		return "", errors.New("binary content cannot be previewed as text")
	}
	if utf8.Valid(data) {
		return string(data), nil
	}
	decoded, e := simplifiedchinese.GB18030.NewDecoder().Bytes(data)
	if e == nil && !bytes.Contains(decoded, []byte("\uFFFD")) {
		return string(decoded), nil
	}
	return "", errors.New("unsupported text encoding; use UTF-8, UTF-16 or GB18030")
}
func archiveText(ext string, data []byte) (string, error) {
	z, e := zip.NewReader(bytes.NewReader(data), int64(len(data)))
	if e != nil {
		return "", errors.New("invalid document archive")
	}
	if len(z.File) > 10000 {
		return "", errors.New("document contains too many archive entries")
	}
	files := map[string]*zip.File{}
	var total uint64
	for _, f := range z.File {
		total += f.UncompressedSize64
		if f.UncompressedSize64 > maxExpanded || total > maxExpanded {
			return "", errors.New("document expands beyond 32 MiB preview limit")
		}
		files[f.Name] = f
	}
	if ext == ".zip" {
		var b strings.Builder
		for _, f := range z.File {
			fmt.Fprintf(&b, "%s\t%d bytes\n", f.Name, f.UncompressedSize64)
		}
		return b.String(), nil
	}
	read := func(name string) ([]byte, error) {
		f := files[name]
		if f == nil {
			return nil, errors.New("document content is missing")
		}
		r, e := f.Open()
		if e != nil {
			return nil, e
		}
		defer r.Close()
		b, e := io.ReadAll(io.LimitReader(r, maxExpanded+1))
		if len(b) > maxExpanded {
			return nil, errors.New("document entry exceeds preview limit")
		}
		return b, e
	}
	var b strings.Builder
	appendXML := func(name string) error {
		data, e := read(name)
		if e != nil {
			return e
		}
		text, e := xmlText(data)
		if e != nil {
			return e
		}
		b.WriteString(text)
		b.WriteByte('\n')
		if b.Len() > maxText {
			return errors.New("document text exceeds 16 MiB preview limit")
		}
		return nil
	}
	switch ext {
	case ".docx", ".docm":
		if e = appendXML("word/document.xml"); e != nil {
			return "", e
		}
		for _, n := range []string{"word/footnotes.xml", "word/endnotes.xml"} {
			if files[n] != nil {
				if e = appendXML(n); e != nil {
					return "", e
				}
			}
		}
	case ".odt", ".ods", ".odp":
		if e = appendXML("content.xml"); e != nil {
			return "", e
		}
	case ".pptx", ".pptm":
		names := []string{}
		for n := range files {
			if strings.HasPrefix(n, "ppt/slides/slide") && strings.HasSuffix(n, ".xml") {
				names = append(names, n)
			}
		}
		sort.Slice(names, func(i, j int) bool { return number(names[i]) < number(names[j]) })
		if len(names) == 0 {
			return "", errors.New("document has no slides")
		}
		for _, n := range names {
			fmt.Fprintf(&b, "\n--- %s ---\n", filepath.Base(n))
			if e = appendXML(n); e != nil {
				return "", e
			}
		}
	case ".xlsx", ".xlsm":
		shared := []string{}
		if files["xl/sharedStrings.xml"] != nil {
			d, e := read("xl/sharedStrings.xml")
			if e != nil {
				return "", e
			}
			shared, e = sharedStrings(d)
			if e != nil {
				return "", e
			}
		}
		names := []string{}
		for n := range files {
			if strings.HasPrefix(n, "xl/worksheets/sheet") && strings.HasSuffix(n, ".xml") {
				names = append(names, n)
			}
		}
		sort.Slice(names, func(i, j int) bool { return number(names[i]) < number(names[j]) })
		if len(names) == 0 {
			return "", errors.New("document has no worksheets")
		}
		for _, n := range names {
			d, e := read(n)
			if e != nil {
				return "", e
			}
			s, e := sheetText(d, shared)
			if e != nil {
				return "", e
			}
			fmt.Fprintf(&b, "\n--- %s ---\n%s", filepath.Base(n), s)
			if b.Len() > maxText {
				return "", errors.New("document text exceeds 16 MiB preview limit")
			}
		}
	case ".epub":
		names := []string{}
		for n := range files {
			if strings.HasSuffix(n, ".xhtml") || strings.HasSuffix(n, ".html") {
				names = append(names, n)
			}
		}
		sort.Strings(names)
		if len(names) == 0 {
			return "", errors.New("document has no readable chapters")
		}
		for _, n := range names {
			fmt.Fprintf(&b, "\n--- %s ---\n", filepath.Base(n))
			if e = appendXML(n); e != nil {
				return "", e
			}
		}
	}
	return b.String(), nil
}
func number(s string) int {
	n := strings.TrimSuffix(filepath.Base(s), ".xml")
	n = strings.TrimLeft(n, "abcdefghijklmnopqrstuvwxyz")
	v, _ := strconv.Atoi(n)
	return v
}
func xmlText(data []byte) (string, error) {
	d := xml.NewDecoder(bytes.NewReader(data))
	var b strings.Builder
	depth := 0
	for {
		t, e := d.Token()
		if e == io.EOF {
			break
		}
		if e != nil {
			return "", errors.New("invalid document XML")
		}
		switch x := t.(type) {
		case xml.StartElement:
			depth++
			if depth > 256 {
				return "", errors.New("document nesting exceeds preview limit")
			}
			if x.Name.Local == "tab" {
				b.WriteByte('\t')
			}
			if x.Name.Local == "br" || x.Name.Local == "cr" {
				b.WriteByte('\n')
			}
		case xml.CharData:
			b.Write([]byte(x))
		case xml.EndElement:
			depth--
			switch x.Name.Local {
			case "p", "tr", "row", "h1", "h2", "div", "section":
				b.WriteByte('\n')
			case "tc", "table-cell":
				b.WriteByte('\t')
			}
		}
		if b.Len() > maxText {
			return "", errors.New("document text exceeds 16 MiB preview limit")
		}
	}
	return b.String(), nil
}
func sharedStrings(data []byte) ([]string, error) {
	d := xml.NewDecoder(bytes.NewReader(data))
	out := []string{}
	var current strings.Builder
	inItem := false
	inText := false
	for {
		t, e := d.Token()
		if e == io.EOF {
			return out, nil
		}
		if e != nil {
			return nil, errors.New("invalid shared strings XML")
		}
		switch x := t.(type) {
		case xml.StartElement:
			if x.Name.Local == "si" {
				inItem = true
				current.Reset()
			}
			if x.Name.Local == "t" {
				inText = true
			}
		case xml.CharData:
			if inItem && inText {
				current.Write(x)
			}
		case xml.EndElement:
			if x.Name.Local == "t" {
				inText = false
			}
			if x.Name.Local == "si" {
				out = append(out, current.String())
				inItem = false
			}
		}
	}
}
func sheetText(data []byte, shared []string) (string, error) {
	type cell struct {
		Ref    string `xml:"r,attr"`
		Kind   string `xml:"t,attr"`
		Value  string `xml:"v"`
		Inline struct {
			Text string `xml:"t"`
		} `xml:"is"`
	}
	d := xml.NewDecoder(bytes.NewReader(data))
	var b strings.Builder
	for {
		t, e := d.Token()
		if e == io.EOF {
			return b.String(), nil
		}
		if e != nil {
			return "", errors.New("invalid worksheet XML")
		}
		switch x := t.(type) {
		case xml.StartElement:
			if x.Name.Local == "c" {
				var c cell
				if e = d.DecodeElement(&c, &x); e != nil {
					return "", errors.New("invalid worksheet cell")
				}
				v := c.Value
				if c.Kind == "s" {
					i, e := strconv.Atoi(v)
					if e == nil && i >= 0 && i < len(shared) {
						v = shared[i]
					}
				}
				if c.Kind == "inlineStr" {
					v = c.Inline.Text
				}
				fmt.Fprintf(&b, "%s: %s\t", c.Ref, v)
			}
		case xml.EndElement:
			if x.Name.Local == "row" {
				b.WriteByte('\n')
			}
		}
		if b.Len() > maxText {
			return "", errors.New("worksheet exceeds preview limit")
		}
	}
}
