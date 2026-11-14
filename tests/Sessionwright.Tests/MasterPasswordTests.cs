using System.IO;
using System.Security.Cryptography;
using Xunit;

namespace Sessionwright.Tests;

// One class: the key in memory is the app's (static), so these tests must not run next to each other.
public sealed class MasterPasswordTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("sessionwright-master").FullName;
    readonly string _rememberFolder = MasterPassword.RememberFolder;

    public MasterPasswordTests() => MasterPassword.RememberFolder = Path.Combine(_dir, "remembered");

    public void Dispose()
    {
        MasterPassword.Lock();
        MasterPassword.RememberFolder = _rememberFolder;
        Directory.Delete(_dir, recursive: true);
    }

    Store NewStore(out Credential credential)
    {
        var store = Store.Load(Path.Combine(_dir, "data.json"));
        credential = new Credential { Name = "Admin", User = "admin" };
        credential.SetPassword("server-secret");
        store.Data.Credentials.Add(credential);
        return store;
    }

    // Set: what Windows protected is sealed with the master password; locked, it cannot be read; the right password
    // (and only that) opens it again, also after the file was loaded anew, as on another PC.
    [Fact]
    public void SealsSavedPasswordsAndOpensThemOnlyWithTheMasterPassword()
    {
        var store = NewStore(out var credential);
        Assert.False(MasterPassword.IsSealed(credential.Secret!));

        Assert.True(MasterPassword.Set(store.Data, "correct horse battery"));
        Assert.True(MasterPassword.IsSealed(credential.Secret!));
        Assert.Equal("server-secret", Store.Unprotect(credential.Secret!));
        store.Save();

        MasterPassword.Lock();
        Assert.Throws<MasterLockedException>(() => Store.Unprotect(credential.Secret!));

        var again = Store.Load(store.Path);
        var master = again.Data.Settings.Master!;
        Assert.False(MasterPassword.TryUnlock(master, "wrong password"));
        Assert.False(MasterPassword.Unlocked);
        Assert.True(MasterPassword.TryUnlock(master, "correct horse battery"));
        Assert.Equal("server-secret", Store.Unprotect(again.Data.Credentials.Single().Secret!));
    }

    // A password saved after the master password was set goes into the file sealed, not under DPAPI.
    [Fact]
    public void APasswordSavedLaterIsSealedOnSave()
    {
        var store = NewStore(out _);
        MasterPassword.Set(store.Data, "correct horse battery");
        var later = new Credential { Name = "Later", User = "later" };
        later.SetPassword("later-secret");
        store.Data.Credentials.Add(later);

        store.Save();

        Assert.True(MasterPassword.IsSealed(later.Secret!));
        Assert.Equal("later-secret", Store.Unprotect(later.Secret!));
    }

    // Locked and not asked (no prompt here): the new password stays under DPAPI rather than being lost, and a change
    // of the master password is refused because it could not read what it has to re-encrypt.
    [Fact]
    public void LockedNothingIsLostAndTheMasterPasswordCannotBeChanged()
    {
        var store = NewStore(out var credential);
        MasterPassword.Set(store.Data, "correct horse battery");
        var master = store.Data.Settings.Master!;
        MasterPassword.Lock();
        var later = new Credential { Name = "Later", User = "later" };
        later.SetPassword("later-secret");
        store.Data.Credentials.Add(later);

        store.Save();
        Assert.False(MasterPassword.IsSealed(later.Secret!));
        Assert.Equal("later-secret", Store.Unprotect(later.Secret!));
        Assert.False(MasterPassword.Set(store.Data, "another password"));
        Assert.False(MasterPassword.Remove(store.Data));
        Assert.Same(master, store.Data.Settings.Master);

        // Entered again: the change goes through and both passwords are under the new one.
        Assert.True(MasterPassword.TryUnlock(master, "correct horse battery"));
        Assert.True(MasterPassword.Set(store.Data, "another password"));
        Assert.False(MasterPassword.TryUnlock(store.Data.Settings.Master!, "correct horse battery"));
        Assert.Equal(("server-secret", "later-secret"), (Store.Unprotect(credential.Secret!), Store.Unprotect(later.Secret!)));
        Assert.All(store.Data.Credentials, c => Assert.True(MasterPassword.IsSealed(c.Secret!)));
    }

    [Fact]
    public void RemovingGoesBackToWindowsProtection()
    {
        var store = NewStore(out var credential);
        MasterPassword.Set(store.Data, "correct horse battery");

        Assert.True(MasterPassword.Remove(store.Data));

        Assert.Null(store.Data.Settings.Master);
        Assert.False(MasterPassword.Unlocked);
        Assert.False(MasterPassword.IsSealed(credential.Secret!));
        Assert.Equal("server-secret", Store.Unprotect(credential.Secret!));
    }

    // "Remember on this PC": the key comes back without the password; locking the passwords forgets it here too.
    [Fact]
    public void RememberedOnThisPcUntilThePasswordsAreLocked()
    {
        var store = NewStore(out var credential);
        MasterPassword.Set(store.Data, "correct horse battery");
        var master = store.Data.Settings.Master!;
        MasterPassword.Remember(master);
        MasterPassword.Lock(); // the app was closed

        Assert.True(MasterPassword.TryRemembered(master));
        Assert.Equal("server-secret", Store.Unprotect(credential.Secret!));

        MasterPassword.Lock(master); // "Lock passwords"
        Assert.False(MasterPassword.TryRemembered(master));
        Assert.Throws<MasterLockedException>(() => Store.Unprotect(credential.Secret!));
    }

    // Notes, when the user chose to seal them: readable in memory, sealed in the file, gone from memory when the
    // passwords are locked, and never lost by a save made while locked.
    [Fact]
    public void NotesAreSealedInTheFileAndLeaveMemoryWhenLocked()
    {
        var store = NewStore(out _);
        var server = new Connection { Name = "Server", Host = "srv01.example.com", Notes = "door code 4711" };
        store.Data.Connections.Add(server);
        MasterPassword.Set(store.Data, "correct horse battery");
        store.Data.Settings.Master!.Notes = true;

        store.Save();
        Assert.Equal("door code 4711", server.Notes); // still there to read
        Assert.Null(server.SealedNotes);
        Assert.DoesNotContain("door code", File.ReadAllText(store.Path));

        MasterPassword.Lock(store.Data);
        Assert.Null(server.Notes);
        Assert.False(MasterPassword.TryOpenNotes(store.Data)); // nobody to ask here
        store.Save(); // locked: what is sealed stays sealed, nothing is dropped

        var again = Store.Load(store.Path);
        var loaded = again.Data.Connections.Single();
        Assert.Null(loaded.Notes);
        Assert.True(MasterPassword.TryUnlock(again.Data.Settings.Master!, "correct horse battery"));
        Assert.True(MasterPassword.TryOpenNotes(again.Data));
        Assert.Equal("door code 4711", loaded.Notes);

        // Without a master password the notes are back in the file as they were.
        Assert.True(MasterPassword.Remove(again.Data));
        again.Save();
        Assert.Contains("door code 4711", File.ReadAllText(again.Path));
        Assert.DoesNotContain("SealedNotes", File.ReadAllText(again.Path));
    }

    // What is in the file is not the password under a reversible disguise: sealed twice, it differs.
    [Fact]
    public void TheFileHoldsNoPasswordInPlainSight()
    {
        var store = NewStore(out var credential);
        MasterPassword.Set(store.Data, "correct horse battery");
        store.Save();

        var saved = File.ReadAllText(store.Path);
        Assert.DoesNotContain("server-secret", saved);
        Assert.DoesNotContain("correct horse battery", saved);
        Assert.IsAssignableFrom<CryptographicException>(Record.Exception(() =>
        {
            MasterPassword.Lock();
            Store.Unprotect(credential.Secret!);
        }));
    }
}
