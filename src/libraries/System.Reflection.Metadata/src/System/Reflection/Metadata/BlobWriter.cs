// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Reflection.Internal;
using System.Runtime.InteropServices;

namespace System.Reflection.Metadata
{
    // TODO: argument checking
    public unsafe struct BlobWriter
    {
        // writable slice:
        private readonly byte[] _buffer;
        private readonly int _start;
        private readonly int _end;  // exclusive

        // position in buffer relative to the beginning of the array:
        private int _position;

        public BlobWriter(int size)
            : this(new byte[size])
        {
        }

        public BlobWriter(byte[] buffer)
            : this(buffer, 0, buffer.Length)
        {
        }

        public BlobWriter(Blob blob)
            : this(blob.Buffer, blob.Start, blob.Length)
        {
        }

        public BlobWriter(byte[] buffer, int start, int count)
        {
            Debug.Assert(buffer != null);
            Debug.Assert(count >= 0);
            Debug.Assert(count <= buffer.Length - start);

            _buffer = buffer;
            _start = start;
            _position = start;
            _end = start + count;
        }

        internal bool IsDefault => _buffer == null;

        /// <summary>
        /// Compares the current content of this writer with another one.
        /// </summary>
        public bool ContentEquals(BlobWriter other)
        {
            return Length == other.Length && ByteSequenceComparer.Equals(_buffer, _start, other._buffer, other._start, Length);
        }

        public int Offset
        {
            get
            {
                return _position - _start;
            }
            set
            {
                if (value < 0 || _start > _end - value)
                {
                    Throw.ValueArgumentOutOfRange();
                }

                _position = _start + value;
            }
        }

        public int Length => _end - _start;
        public int RemainingBytes => _end - _position;
        public Blob Blob => new Blob(_buffer, _start, Length);

        public byte[] ToArray()
        {
            return ToArray(0, Offset);
        }

        /// <exception cref="ArgumentOutOfRangeException">Range specified by <paramref name="start"/> and <paramref name="byteCount"/> falls outside of the bounds of the buffer content.</exception>
        public byte[] ToArray(int start, int byteCount)
        {
            BlobUtilities.ValidateRange(Length, start, byteCount, nameof(byteCount));

            var result = new byte[byteCount];
            Array.Copy(_buffer, _start + start, result, 0, byteCount);
            return result;
        }

        public ImmutableArray<byte> ToImmutableArray()
        {
            return ToImmutableArray(0, Offset);
        }

        /// <exception cref="ArgumentOutOfRangeException">Range specified by <paramref name="start"/> and <paramref name="byteCount"/> falls outside of the bounds of the buffer content.</exception>
        public ImmutableArray<byte> ToImmutableArray(int start, int byteCount)
        {
            byte[]? array = ToArray(start, byteCount);
            return ImmutableByteArrayInterop.DangerousCreateFromUnderlyingArray(ref array);
        }

        private int Advance(int value)
        {
            Debug.Assert(value >= 0);

            int position = _position;
            if (position > _end - value)
            {
                Throw.OutOfBounds();
            }

            _position = position + value;
            return position;
        }

        /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteCount"/> is negative.</exception>
        public void WriteBytes(byte value, int byteCount)
        {
            if (byteCount < 0)
            {
                Throw.ArgumentOutOfRange(nameof(byteCount));
            }

            int start = Advance(byteCount);
            fixed (byte* buffer = _buffer)
            {
                byte* ptr = buffer + start;
                for (int i = 0; i < byteCount; i++)
                {
                    ptr[i] = value;
                }
            }
        }

        /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteCount"/> is negative.</exception>
        public unsafe void WriteBytes(byte* buffer, int byteCount)
        {
            if (buffer == null)
            {
                Throw.ArgumentNull(nameof(buffer));
            }

            if (byteCount < 0)
            {
                Throw.ArgumentOutOfRange(nameof(byteCount));
            }

            WriteBytesUnchecked(buffer, byteCount);
        }

        private unsafe void WriteBytesUnchecked(byte* buffer, int byteCount)
        {
            int start = Advance(byteCount);
            Marshal.Copy((IntPtr)buffer, _buffer, start, byteCount);
        }

        /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
        public void WriteBytes(BlobBuilder source)
        {
            if (source == null)
            {
                Throw.ArgumentNull(nameof(source));
            }

            source.WriteContentTo(ref this);
        }

        /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteCount"/> is negative.</exception>
        public int WriteBytes(Stream source, int byteCount)
        {
            if (source == null)
            {
                Throw.ArgumentNull(nameof(source));
            }

            if (byteCount < 0)
            {
                Throw.ArgumentOutOfRange(nameof(byteCount));
            }

            int start = Advance(byteCount);
            int bytesRead = source.TryReadAll(_buffer, start, byteCount);
            _position = start + bytesRead;
            return bytesRead;
        }

        /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is null.</exception>
        public void WriteBytes(ImmutableArray<byte> buffer)
        {
            WriteBytes(buffer, 0, buffer.IsDefault ? 0 : buffer.Length);
        }

        /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Range specified by <paramref name="start"/> and <paramref name="byteCount"/> falls outside of the bounds of the <paramref name="buffer"/>.</exception>
        public void WriteBytes(ImmutableArray<byte> buffer, int start, int byteCount)
        {
            WriteBytes(ImmutableByteArrayInterop.DangerousGetUnderlyingArray(buffer)!, start, byteCount);
        }

        /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is null.</exception>
        public unsafe void WriteBytes(byte[] buffer)
        {
            WriteBytes(buffer, 0, buffer?.Length ?? 0);
        }

        /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Range specified by <paramref name="start"/> and <paramref name="byteCount"/> falls outside of the bounds of the <paramref name="buffer"/>.</exception>
        public unsafe void WriteBytes(byte[] buffer, int start, int byteCount)
        {
            if (buffer == null)
            {
                Throw.ArgumentNull(nameof(buffer));
            }

            BlobUtilities.ValidateRange(buffer.Length, start, byteCount, nameof(byteCount));

            // an empty array has no element pointer:
            if (buffer.Length == 0)
            {
                return;
            }

            fixed (byte* ptr = &buffer[0])
            {
                WriteBytes(ptr + start, byteCount);
            }
        }

        public void PadTo(int offset)
        {
            WriteBytes(0, offset - Offset);
        }

        public void Align(int alignment)
        {
            int offset = Offset;
            WriteBytes(0, BitArithmetic.Align(offset, alignment) - offset);
        }

        public void WriteBoolean(bool value)
        {
            WriteByte((byte)(value ? 1 : 0));
        }

        public void WriteByte(byte value)
        {
            int start = Advance(sizeof(byte));
            _buffer[start] = value;
        }

        public void WriteSByte(sbyte value)
        {
            WriteByte(unchecked((byte)value));
        }

        public void WriteDouble(double value)
        {
            int start = Advance(sizeof(double));
            _buffer.WriteDouble(start, value);
        }

        public void WriteSingle(float value)
        {
            int start = Advance(sizeof(float));
            _buffer.WriteSingle(start, value);
        }

        public void WriteInt16(short value)
        {
            WriteUInt16(unchecked((ushort)value));
        }

        public void WriteUInt16(ushort value)
        {
            int start = Advance(sizeof(ushort));
            _buffer.WriteUInt16(start, value);
        }

        public void WriteInt16BE(short value)
        {
            WriteUInt16BE(unchecked((ushort)value));
        }

        public void WriteUInt16BE(ushort value)
        {
            int start = Advance(sizeof(ushort));
            _buffer.WriteUInt16BE(start, value);
        }

        public void WriteInt32BE(int value)
        {
            WriteUInt32BE(unchecked((uint)value));
        }

        public void WriteUInt32BE(uint value)
        {
            int start = Advance(sizeof(uint));
            _buffer.WriteUInt32BE(start, value);
        }

        public void WriteInt32(int value)
        {
            WriteUInt32(unchecked((uint)value));
        }

        public void WriteUInt32(uint value)
        {
            int start = Advance(sizeof(uint));
            _buffer.WriteUInt32(start, value);
        }

        public void WriteInt64(long value)
        {
            WriteUInt64(unchecked((ulong)value));
        }

        public void WriteUInt64(ulong value)
        {
            int start = Advance(sizeof(ulong));
            _buffer.WriteUInt64(start, value);
        }

        public void WriteDecimal(decimal value)
        {
            int start = Advance(BlobUtilities.SizeOfSerializedDecimal);
            _buffer.WriteDecimal(start, value);
        }

        public void WriteGuid(Guid value)
        {
            int start = Advance(BlobUtilities.SizeOfGuid);
            _buffer.WriteGuid(start, value);
        }

        public void WriteDateTime(DateTime value)
        {
            WriteInt64(value.Ticks);
        }

        /// <summary>
        /// Writes a reference to a heap (heap offset) or a table (row number).
        /// </summary>
        /// <param name="reference">Heap offset or table row number.</param>
        /// <param name="isSmall">True to encode the reference as 16-bit integer, false to encode as 32-bit integer.</param>
        public void WriteReference(int reference, bool isSmall)
        {
            // This code is a very hot path, hence we don't check if the reference actually fits 2B.

            if (isSmall)
            {
                Debug.Assert(unchecked((ushort)reference) == reference);
                WriteUInt16((ushort)reference);
            }
            else
            {
                WriteInt32(reference);
            }
        }

        /// <summary>
        /// Writes UTF16 (little-endian) encoded string at the current position.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        public void WriteUTF16(char[] value)
        {
            if (value == null)
            {
                Throw.ArgumentNull(nameof(value));
            }

            if (value.Length == 0)
            {
                return;
            }

            if (BitConverter.IsLittleEndian)
            {
                fixed (char* ptr = &value[0])
                {
                    WriteBytesUnchecked((byte*)ptr, value.Length * sizeof(char));
                }
            }
            else
            {
                for (int i = 0; i < value.Length; i++)
                {
                    WriteUInt16((ushort)value[i]);
                }
            }
        }

        /// <summary>
        /// Writes UTF16 (little-endian) encoded string at the current position.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        public void WriteUTF16(string value)
        {
            if (value == null)
            {
                Throw.ArgumentNull(nameof(value));
            }

            if (BitConverter.IsLittleEndian)
            {
                fixed (char* ptr = value)
                {
                    WriteBytesUnchecked((byte*)ptr, value.Length * sizeof(char));
                }
            }
            else
            {
                for (int i = 0; i < value.Length; i++)
                {
                    WriteUInt16((ushort)value[i]);
                }
            }
        }

        /// <summary>
        /// Writes string in SerString format (see ECMA-335-II 23.3 Custom attributes).
        /// </summary>
        /// <remarks>
        /// The string is UTF8 encoded and prefixed by the its size in bytes.
        /// Null string is represented as a single byte 0xFF.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Builder is not writable, it has been linked with another one.</exception>
        public void WriteSerializedString(string? str)
        {
            if (str == null)
            {
                WriteByte(0xff);
                return;
            }

            WriteUTF8(str, 0, str.Length, allowUnpairedSurrogates: true, prependSize: true);
        }

        /// <summary>
        /// Writes string in User String (#US) heap format (see ECMA-335-II 24.2.4 #US and #Blob heaps):
        /// </summary>
        /// <remarks>
        /// The string is UTF16 encoded and prefixed by the its size in bytes.
        ///
        /// This final byte holds the value 1 if and only if any UTF16 character within the string has any bit set in its top byte,
        /// or its low byte is any of the following: 0x01-0x08, 0x0E-0x1F, 0x27, 0x2D, 0x7F. Otherwise, it holds 0.
        /// The 1 signifies Unicode characters that require handling beyond that normally provided for 8-bit encoding sets.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Builder is not writable, it has been linked with another one.</exception>
        public void WriteUserString(string value!!)
        {
            WriteCompressedInteger(BlobUtilities.GetUserStringByteLength(value.Length));
            WriteUTF16(value);
            WriteByte(BlobUtilities.GetUserStringTrailingByte(value));
        }

        /// <summary>
        /// Writes UTF8 encoded string at the current position.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        public void WriteUTF8(string value, bool allowUnpairedSurrogates)
        {
            if (value == null)
            {
                Throw.ArgumentNull(nameof(value));
            }

            WriteUTF8(value, 0, value.Length, allowUnpairedSurrogates, prependSize: false);
        }

        private void WriteUTF8(string str, int start, int length, bool allowUnpairedSurrogates, bool prependSize)
        {
            fixed (char* strPtr = str)
            {
                char* charPtr = strPtr + start;
                int byteCount = BlobUtilities.GetUTF8ByteCount(charPtr, length);

                if (prependSize)
                {
                    WriteCompressedInteger(byteCount);
                }

                int startOffset = Advance(byteCount);
                _buffer.WriteUTF8(startOffset, charPtr, length, byteCount, allowUnpairedSurrogates);
            }
        }

        /// <summary>
        /// Implements compressed signed integer encoding as defined by ECMA-335-II chapter 23.2: Blobs and signatures.
        /// </summary>
        /// <remarks>
        /// If the value lies between -64 (0xFFFFFFC0) and 63 (0x3F), inclusive, encode as a one-byte integer:
        /// bit 7 clear, value bits 5 through 0 held in bits 6 through 1, sign bit (value bit 31) in bit 0.
        ///
        /// If the value lies between -8192 (0xFFFFE000) and 8191 (0x1FFF), inclusive, encode as a two-byte integer:
        /// 15 set, bit 14 clear, value bits 12 through 0 held in bits 13 through 1, sign bit(value bit 31) in bit 0.
        ///
        /// If the value lies between -268435456 (0xF000000) and 268435455 (0x0FFFFFFF), inclusive, encode as a four-byte integer:
        /// 31 set, 30 set, bit 29 clear, value bits 27 through 0 held in bits 28 through 1, sign bit(value bit 31) in bit 0.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> can't be represented as a compressed signed integer.</exception>
        public void WriteCompressedSignedInteger(int value)
        {
            BlobWriterImpl.WriteCompressedSignedInteger(ref this, value);
        }

        /// <summary>
        /// Implements compressed unsigned integer encoding as defined by ECMA-335-II chapter 23.2: Blobs and signatures.
        /// </summary>
        /// <remarks>
        /// If the value lies between 0 (0x00) and 127 (0x7F), inclusive,
        /// encode as a one-byte integer (bit 7 is clear, value held in bits 6 through 0).
        ///
        /// If the value lies between 28 (0x80) and 214 - 1 (0x3FFF), inclusive,
        /// encode as a 2-byte integer with bit 15 set, bit 14 clear(value held in bits 13 through 0).
        ///
        /// Otherwise, encode as a 4-byte integer, with bit 31 set, bit 30 set, bit 29 clear (value held in bits 28 through 0).
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> can't be represented as a compressed unsigned integer.</exception>
        public void WriteCompressedInteger(int value)
        {
            BlobWriterImpl.WriteCompressedInteger(ref this, unchecked((uint)value));
        }

        /// <summary>
        /// Writes a constant value (see ECMA-335 Partition II section 22.9) at the current position.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="value"/> is not of a constant type.</exception>
        public void WriteConstant(object? value)
        {
            BlobWriterImpl.WriteConstant(ref this, value);
        }

        public void Clear()
        {
            _position = _start;
        }
    }
}
