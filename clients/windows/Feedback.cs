using System.Text.RegularExpressions;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
namespace ClipWeave;
sealed class ErrorSpeaker:IDisposable {
    MediaPlayer? player;SpeechSynthesizer? speech;SpeechSynthesisStream? current;
    string last="";DateTime spoken=DateTime.MinValue;
    public async Task Say(string text){if(last==text&&DateTime.UtcNow-spoken<TimeSpan.FromSeconds(60))return;last=text;spoken=DateTime.UtcNow;try{player??=new();speech??=new();var language=System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;var voice=SpeechSynthesizer.AllVoices.FirstOrDefault(v=>v.Language.StartsWith(language,StringComparison.OrdinalIgnoreCase));if(voice!=null)speech.Voice=voice;var stream=await speech.SynthesizeTextToStreamAsync(text);player.Pause();current?.Dispose();current=stream;player.Source=MediaSource.CreateFromStream(stream,stream.ContentType);player.Play();}catch{/* Keep readable feedback when an offline voice is unavailable. */}}
    public void Dispose(){player?.Dispose();current?.Dispose();speech?.Dispose();}
}
