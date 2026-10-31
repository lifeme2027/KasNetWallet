using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;

namespace KaspaWallet.Core.Keystore;

public sealed class WrongPasswordException : Exception
{
    public WrongPasswordException() : base("Wrong password or corrupted keystore.") { }
}

/// <summary>
/// Encrypts the BIP39 mnemonic with AES-256-GCM under a key from Argon2id(password, salt).
/// Associated data binds version, network and address. Byte buffers holding secrets are zeroed after use;
/// .NET strings (the mnemonic/password as typed) cannot be reliably zeroed — see docs/DECISIONS.md (D5).
/// </summary>
public static class WalletKeystore
{
    private const int SaltLen = 16, NonceLen = 12, TagLen = 16, KeyLen = 32;
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static KeystoreFile Encrypt(string mnemonic, string password, WalletNetwork network, KdfParameters? kdf = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        kdf ??= KdfParameters.Default;
        kdf.Validate();
        var normalized = MnemonicService.Normalize(mnemonic);
        var address = HdAccount.DeriveAddress(normalized, network);

        var file = new KeystoreFile
        {
            Argon2MemoryKiB = kdf.MemoryKiB,
            Argon2Iterations = kdf.Iterations,
            Argon2Parallelism = kdf.Parallelism,
            Network = network.ToString(),
            Address = address,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        var salt = RandomNumberGenerator.GetBytes(SaltLen);
        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var plain = Encoding.UTF8.GetBytes(normalized);
        var key = DeriveKey(password, salt, kdf);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagLen];
        try
        {
            using var gcm = new AesGcm(key, TagLen);
            gcm.Encrypt(nonce, plain, cipher, tag, AssociatedData(file));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
        }
        file.Salt = Convert.ToBase64String(salt);
        file.Nonce = Convert.ToBase64String(nonce);
        file.Ciphertext = Convert.ToBase64String(cipher);
        file.Tag = Convert.ToBase64String(tag);
        return file;
    }

    /// <summary>Returns the mnemonic. Caller must not log it and should drop the reference ASAP.</summary>
    public static string Decrypt(KeystoreFile file, string password)
    {
        if (file.Version != 1 || file.Kdf != "argon2id" || file.Cipher != "aes-256-gcm")
            throw new InvalidDataException("Unsupported keystore format.");
        var kdf = new KdfParameters(file.Argon2MemoryKiB, file.Argon2Iterations, file.Argon2Parallelism);
        kdf.Validate();
        var salt = Convert.FromBase64String(file.Salt);
        var nonce = Convert.FromBase64String(file.Nonce);
        var cipher = Convert.FromBase64String(file.Ciphertext);
        var tag = Convert.FromBase64String(file.Tag);
        if (salt.Length != SaltLen || nonce.Length != NonceLen || tag.Length != TagLen) throw new InvalidDataException("Corrupted keystore.");
        var plain = new byte[cipher.Length];
        var key = DeriveKey(password ?? "", salt, kdf);
        try
        {
            using var gcm = new AesGcm(key, TagLen);
            gcm.Decrypt(nonce, cipher, tag, plain, AssociatedData(file));
            return Encoding.UTF8.GetString(plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new WrongPasswordException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Decrypts and derives the account; also checks the stored address matches.</summary>
    public static HdAccount Unlock(KeystoreFile file, string password)
    {
        var network = Enum.Parse<WalletNetwork>(file.Network);
        var mnemonic = Decrypt(file, password);
        var acc = HdAccount.FromMnemonic(mnemonic, network);
        if (acc.Address != file.Address)
        {
            acc.Dispose();
            throw new InvalidDataException("Keystore address does not match the decrypted key.");
        }
        return acc;
    }

    public static void Save(KeystoreFile file, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, Opts));
        File.Move(tmp, path, overwrite: true);
    }

    public static KeystoreFile Load(string path) =>
        JsonSerializer.Deserialize<KeystoreFile>(File.ReadAllText(path), Opts) ?? throw new InvalidDataException("Empty keystore.");

    private static byte[] AssociatedData(KeystoreFile f) =>
        Encoding.UTF8.GetBytes($"kdw-keystore|v{f.Version}|{f.Network}|{f.Address}|{f.Argon2MemoryKiB}|{f.Argon2Iterations}|{f.Argon2Parallelism}");

    private static byte[] DeriveKey(string password, byte[] salt, KdfParameters kdf)
    {
        var pw = Encoding.UTF8.GetBytes(password);
        try
        {
            using var argon = new Argon2id(pw)
            {
                Salt = salt,
                MemorySize = kdf.MemoryKiB,
                Iterations = kdf.Iterations,
                DegreeOfParallelism = kdf.Parallelism,
            };
            return argon.GetBytes(KeyLen);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pw);
        }
    }
}
