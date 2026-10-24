package preview

import (
	"archive/zip"
	"bytes"
	"strings"
	"testing"
)

func archive(t *testing.T, files map[string]string) []byte {
	t.Helper()
	var b bytes.Buffer
	z := zip.NewWriter(&b)
	for n, s := range files {
		w, e := z.Create(n)
		if e != nil {
			t.Fatal(e)
		}
		w.Write([]byte(s))
	}
	if e := z.Close(); e != nil {
		t.Fatal(e)
	}
	return b.Bytes()
}
func TestFullText(t *testing.T) {
	s := strings.Repeat("长文本🌍\n", 20000) + "END-OF-DOCUMENT"
	r, e := Document("notes.txt", []byte(s))
	if e != nil || r.Text != s {
		t.Fatal("long text truncated", e)
	}
}
func TestWord(t *testing.T) {
	b := archive(t, map[string]string{"word/document.xml": `<w:document xmlns:w="w"><w:body><w:p><w:r><w:t>第一段</w:t></w:r></w:p><w:p><w:r><w:t>END</w:t></w:r></w:p></w:body></w:document>`})
	r, e := Document("memo.docx", b)
	if e != nil || !strings.Contains(r.Text, "第一段\n") || !strings.Contains(r.Text, "END") {
		t.Fatal(r, e)
	}
}
func TestSheet(t *testing.T) {
	b := archive(t, map[string]string{"xl/sharedStrings.xml": `<sst><si><t>中文单元格</t></si></sst>`, "xl/worksheets/sheet1.xml": `<worksheet><sheetData><row><c r="A1" t="s"><v>0</v></c><c r="B1"><v>42</v></c></row></sheetData></worksheet>`})
	r, e := Document("table.xlsx", b)
	if e != nil || !strings.Contains(r.Text, "中文单元格") || !strings.Contains(r.Text, "42") {
		t.Fatal(r, e)
	}
}
func TestSlidesOrder(t *testing.T) {
	b := archive(t, map[string]string{"ppt/slides/slide10.xml": `<p><t>TEN</t></p>`, "ppt/slides/slide2.xml": `<p><t>TWO</t></p>`})
	r, e := Document("deck.pptx", b)
	if e != nil || strings.Index(r.Text, "TWO") > strings.Index(r.Text, "TEN") {
		t.Fatal(r, e)
	}
}
func TestUTF16(t *testing.T) {
	r, e := Document("notes.txt", []byte{255, 254, 0x2d, 0x4e, 0x87, 0x65})
	if e != nil || r.Text != "中文" {
		t.Fatal(r, e)
	}
}
func TestCorruptAndBinary(t *testing.T) {
	if _, e := Document("bad.docx", []byte("not zip")); e == nil {
		t.Fatal("corrupt Office file accepted")
	}
	r, e := Document("app.exe", []byte{0, 1, 2})
	if e != nil || r.Kind != "unsupported" {
		t.Fatal(r, e)
	}
}
func TestExpansionLimit(t *testing.T) {
	b := archive(t, map[string]string{"word/document.xml": strings.Repeat("x", maxExpanded+1)})
	if _, e := Document("large.docx", b); e == nil {
		t.Fatal("zip expansion limit bypassed")
	}
}

func TestGB18030(t *testing.T) {
	r, e := Document("中文.txt", []byte{0xd6, 0xd0, 0xce, 0xc4})
	if e != nil || r.Text != "中文" {
		t.Fatal(r, e)
	}
}
