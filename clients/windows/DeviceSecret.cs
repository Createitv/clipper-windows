using System.Security.Cryptography;
using Windows.Security.Credentials;
namespace ClipWeave;
// Windows Credential Locker protects the per-installation vault unlock secret.
static class DeviceSecret {
    const string Resource="dev.cliper.vault-unlock";
    static readonly string Scope=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ClipWeave","vault");
    public static string? Read() {
        var vault=new PasswordVault();
        // FindAllByResource reports ERROR_NOT_FOUND only when no saved credential exists.
        try { var credential=vault.Retrieve(Resource,Scope);credential.RetrievePassword();return credential.Password; }
        catch(System.Runtime.InteropServices.COMException e) when(e.HResult==unchecked((int)0x80070490)) {return null;}
    }
    public static void Save(string value) {if(Read()!=null)return;new PasswordVault().Add(new PasswordCredential(Resource,Scope,value));}
    // The caller must first successfully validate this exact password with the vault.
    public static void SaveVerified(string value) {
        var vault=new PasswordVault();PasswordCredential? previous=null;
        if(Read() is string prior) previous=new PasswordCredential(Resource,Scope,prior);
        if(previous!=null)vault.Remove(previous);
        try {vault.Add(new PasswordCredential(Resource,Scope,value));}
        catch {if(previous!=null)vault.Add(previous);throw;}
    }
    public static string GetOrCreate() {var value=Read();if(value!=null)return value;value=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));Save(value);return value;}
}
