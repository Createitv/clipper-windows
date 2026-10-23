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
    public static string PendingKey() {
        const string resource="dev.cliper.pending-session";var vault=new PasswordVault();
        try{var credential=vault.Retrieve(resource,Scope);credential.RetrievePassword();if(Convert.FromBase64String(credential.Password).Length!=32)throw new IOException("Invalid session wrapping key");return credential.Password;}
        catch(System.Runtime.InteropServices.COMException e) when(e.HResult==unchecked((int)0x80070490)){var key=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));vault.Add(new PasswordCredential(resource,Scope,key));return key;}
    }
    public static void ResetPendingKey(){var vault=new PasswordVault();try{var credential=vault.Retrieve("dev.cliper.pending-session",Scope);vault.Remove(credential);}catch(System.Runtime.InteropServices.COMException e) when(e.HResult==unchecked((int)0x80070490)){}vault.Add(new PasswordCredential("dev.cliper.pending-session",Scope,Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));}
}
