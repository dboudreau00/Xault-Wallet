using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace XaultWallet.Core.Security;

/// <summary>
/// A byte buffer that pins its memory (so the GC cannot move / copy it) and
/// zeroes it on disposal. Use it for anything derived from a password:
/// raw password bytes, derived keys, decrypted secrets.
///
/// NOTE: In a managed runtime this is best-effort. The CLR may still create
/// transient copies (e.g. when you build the password string in the first
/// place). We minimise the window; we cannot fully eliminate it. See SECURITY.md.
/// </summary>
public sealed class SecureBuffer : IDisposable
{
    private readonly byte[] _data;
    private GCHandle _handle;
    private bool _disposed;

    public SecureBuffer(int length)
    {
        _data = new byte[length];
        _handle = GCHandle.Alloc(_data, GCHandleType.Pinned);
    }

    public SecureBuffer(ReadOnlySpan<byte> source) : this(source.Length)
    {
        source.CopyTo(_data);
    }

    public int Length => _data.Length;

    public Span<byte> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _data;
        }
    }

    public byte[] UnsafeArray
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _data;
        }
    }

    /// <summary>Copies out a plain array. Caller becomes responsible for wiping it.</summary>
    public byte[] ToArray() => Span.ToArray();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_data);
        if (_handle.IsAllocated)
        {
            _handle.Free();
        }

        _disposed = true;
    }

    /// <summary>Convert a UTF-8 password char[] into a pinned/zeroable byte buffer, wiping the source chars.</summary>
    public static SecureBuffer FromPassword(char[] password)
    {
        int byteCount = System.Text.Encoding.UTF8.GetByteCount(password);
        var buffer = new SecureBuffer(byteCount);
        System.Text.Encoding.UTF8.GetBytes(password, buffer.Span);
        Array.Clear(password, 0, password.Length);
        return buffer;
    }
}
