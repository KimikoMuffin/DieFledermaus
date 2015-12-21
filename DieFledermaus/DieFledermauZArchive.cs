﻿#region BSD license
/*
Copyright © 2015, KimikoMuffin.
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer. 
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.
3. The names of its contributors may not be used to endorse or promote 
   products derived from this software without specific prior written 
   permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/
#endregion

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

using DieFledermaus.Globalization;
using Org.BouncyCastle.Crypto;
using SevenZip;

namespace DieFledermaus
{
#if COMPLVL
    using System.IO.Compression;
#endif
    /// <summary>
    /// Represents a DieFledermauZ archive file.
    /// </summary>
    /// <remarks>
    /// If this class attempts to load a stream containing a valid <see cref="DieFledermausStream"/>, it will interpret the stream as an archive containing 
    /// a single entry with the path set to the <see cref="DieFledermausStream.Filename"/>, or a <c>null</c> path if the DieFledermaus stream does not have
    /// a filename set.
    /// </remarks>
    public class DieFledermauZArchive : IDisposable, IMausCrypt, IMausProgress
    {
        private const int _mHead = 0x5a75416d;
        private const int _allEntries = 0x54414403, _curEntry = 0x74616403, _allOffsets = 0x52455603, _curOffset = 0x72657603;
        private const ushort _versionShort = 40, _minVersionShort = _versionShort;

        private bool _leaveOpen;
        private Stream _baseStream;
        /// <summary>
        /// Gets the underlying stream used by the current instance.
        /// </summary>
        public Stream BaseStream { get { return _baseStream; } }

        [NonSerialized]
        internal readonly object _lock = new object();

        private bool _headerGotten;
        internal readonly long StreamOffset;

        private readonly List<DieFledermauZItem> _entries = new List<DieFledermauZItem>();
        private readonly Dictionary<string, int> _entryDict = new Dictionary<string, int>(StringComparer.Ordinal);

        #region Constructors
        /// <summary>
        /// Creates a new instance using the specified options.
        /// </summary>
        /// <param name="stream">The stream containing the DieFledermauZ archive.</param>
        /// <param name="mode">Indicates options for accessing the stream.</param>
        /// <param name="leaveOpen"><c>true</c> to leave <paramref name="stream"/> open when the current instance is disposed;
        /// <c>false</c> to close <paramref name="stream"/>.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="stream"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <para><paramref name="mode"/> is <see cref="MauZArchiveMode.Create"/>, and <paramref name="stream"/> does not support writing.</para>
        /// <para>-OR-</para>
        /// <para><paramref name="mode"/> is <see cref="MauZArchiveMode.Read"/>, and <paramref name="stream"/> does not support reading.</para>
        /// </exception>
        /// <exception cref="ObjectDisposedException">
        /// <paramref name="stream"/> is closed.
        /// </exception>
        /// <exception cref="InvalidDataException">
        /// <paramref name="mode"/> is <see cref="MauZArchiveMode.Read"/>, and <paramref name="stream"/> does not contain either a valid DieFledermauZ archive
        /// or a valid <see cref="DieFledermausStream"/>.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// <paramref name="mode"/> is <see cref="MauZArchiveMode.Read"/>, and <paramref name="stream"/> contains unsupported options.
        /// </exception>
        /// <exception cref="IOException">
        /// An I/O error occurred.
        /// </exception>
        public DieFledermauZArchive(Stream stream, MauZArchiveMode mode, bool leaveOpen)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (mode == MauZArchiveMode.Create)
            {
                DieFledermausStream.CheckWrite(stream);
                _baseStream = stream;
                _mode = mode;
                _entriesRO = new EntryList(this);
            }
            else if (mode == MauZArchiveMode.Read)
            {
                DieFledermausStream.CheckRead(stream);
                _baseStream = stream;
                if (stream.CanSeek)
                {
                    if (stream.Length == stream.Position) stream.Seek(0, SeekOrigin.Begin);
                    StreamOffset = stream.Position;
                }
                _mode = mode;
                ReadHeader();
            }
            else throw new InvalidEnumArgumentException(nameof(mode), (int)mode, typeof(MauZArchiveMode));

            _leaveOpen = leaveOpen;
        }

        /// <summary>
        /// Creates a new instance using the specified options.
        /// </summary>
        /// <param name="stream">The stream containing the DieFledermauZ archive.</param>
        /// <param name="mode">Indicates options for accessing the stream.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="stream"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <para><paramref name="mode"/> is <see cref="MauZArchiveMode.Create"/>, and <paramref name="stream"/> does not support writing.</para>
        /// <para>-OR-</para>
        /// <para><paramref name="mode"/> is <see cref="MauZArchiveMode.Read"/>, and <paramref name="stream"/> does not support reading.</para>
        /// </exception>
        /// <exception cref="ObjectDisposedException">
        /// <paramref name="stream"/> is closed.
        /// </exception>
        /// <exception cref="InvalidDataException">
        /// <paramref name="mode"/> is <see cref="MauZArchiveMode.Read"/>, and <paramref name="stream"/> does not contain either a valid DieFledermauZ archive
        /// or a valid <see cref="DieFledermausStream"/>.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// <paramref name="mode"/> is <see cref="MauZArchiveMode.Read"/>, and <paramref name="stream"/> contains unsupported options.
        /// </exception>
        /// <exception cref="IOException">
        /// An I/O error occurred.
        /// </exception>
        public DieFledermauZArchive(Stream stream, MauZArchiveMode mode)
            : this(stream, mode, false)
        {
        }

        /// <summary>
        /// Creates a new instance in create-mode using the specified encryption format.
        /// </summary>
        /// <param name="stream">The stream containing the DieFledermauZ archive.</param>
        /// <param name="encryptionFormat">Indicates options for how to encrypt the stream.</param>
        /// <param name="leaveOpen"><c>true</c> to leave <paramref name="stream"/> open when the current instance is disposed;
        /// <c>false</c> to close <paramref name="stream"/>.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="stream"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="stream"/> does not support writing.
        /// </exception>
        /// <exception cref="ObjectDisposedException">
        /// <paramref name="stream"/> is closed.
        /// </exception>
        /// <exception cref="IOException">
        /// An I/O error occurred.
        /// </exception>
        public DieFledermauZArchive(Stream stream, MausEncryptionFormat encryptionFormat, bool leaveOpen)
            : this(stream, MauZArchiveMode.Create, leaveOpen)
        {
            _setEncFormat(encryptionFormat);
        }

        /// <summary>
        /// Creates a new instance in create-mode using the specified encryption format.
        /// </summary>
        /// <param name="stream">The stream containing the DieFledermauZ archive.</param>
        /// <param name="encryptionFormat">Indicates options for how to encrypt the stream.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="stream"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="stream"/> does not support writing.
        /// </exception>
        /// <exception cref="ObjectDisposedException">
        /// <paramref name="stream"/> is closed.
        /// </exception>
        /// <exception cref="IOException">
        /// An I/O error occurred.
        /// </exception>
        public DieFledermauZArchive(Stream stream, MausEncryptionFormat encryptionFormat)
            : this(stream, MauZArchiveMode.Create, false)
        {
            _setEncFormat(encryptionFormat);
        }

        private void _setEncFormat(MausEncryptionFormat encryptionFormat)
        {
            _keySizes = DieFledermausStream._getKeySizes(encryptionFormat, out _blockByteCount);
            _encFmt = encryptionFormat;
            if (encryptionFormat == MausEncryptionFormat.None) return;
            _keySize = _keySizes.MaxSize;
            _iv = DieFledermausStream.FillBuffer(_blockByteCount);
            _salt = DieFledermausStream.FillBuffer(_keySize >> 3);
            _encryptedOptions = new SettableOptions(this);
        }
        #endregion

        long totalSize, curOffset;

        private void ReadHeader()
        {
#if NOLEAVEOPEN
            BinaryReader reader = new BinaryReader(_baseStream);
#else
            using (BinaryReader reader = new BinaryReader(_baseStream, DieFledermausStream._textEncoding, true))
#endif
            {
                int head = reader.ReadInt32();

                if (head == DieFledermausStream._head)
                {
                    long skipOffset = 0;
                    _entries.Add(LoadMausStream(_baseStream, null, false, -1, 0, ref skipOffset));
                    return;
                }
                else if (head != _mHead)
                    throw new InvalidDataException(TextResources.InvalidDataMauZ);

                ushort version = reader.ReadUInt16();

                if (version < _minVersionShort)
                    throw new NotSupportedException(TextResources.VersionTooLowZ);
                if (version > _versionShort)
                    throw new NotSupportedException(TextResources.VersionTooHighZ);

                totalSize = reader.ReadInt64();

                if (totalSize < BaseOffset)
                    throw new InvalidDataException(TextResources.InvalidDataMauZ);
                curOffset = BaseOffset;
                _hashFunc = MausHashFunction.Sha512;
                ReadOptions(reader, false);

                if (_encFmt == MausEncryptionFormat.None)
                {
                    ReadDecrypted(reader, ref curOffset);
                    //Size of metaoffset
                    if ((curOffset + sizeof(long)) != totalSize)
                        throw new InvalidDataException(TextResources.InvalidDataMauZ);

                    return;
                }

                long pkValue = reader.ReadInt64();

                if (pkValue < 0 || pkValue > (int.MaxValue - DieFledermausStream.minPkCount))
                    throw new InvalidDataException(TextResources.InvalidDataMauZ);
                _pkCount = (int)pkValue;

                int hashSize = DieFledermausStream.GetHashLength(_hashFunc);

                _hmacExpected = DieFledermausStream.ReadBytes(reader, hashSize);
                _salt = DieFledermausStream.ReadBytes(reader, _keySizes.MaxSize >> 3);
                _iv = DieFledermausStream.ReadBytes(reader, _blockByteCount);

                curOffset += hashSize + _salt.Length + _blockByteCount - 4;
            }
        }

        private void ReadDecrypted(BinaryReader reader, ref long curOffset)
        {
            _entriesRO = new EntryList(this);
            _headerGotten = true;
            long entryCount = reader.ReadInt64();
            if (entryCount <= 0)
                throw new InvalidDataException(TextResources.InvalidDataMauZ);

            DieFledermauZItem[] entries = new DieFledermauZItem[entryCount];

            //All Entries
            if (reader.ReadInt32() != _allEntries)
                throw new InvalidDataException(TextResources.InvalidDataMauZ);

            for (long i = 0; i < entryCount; i++)
            {
                long curBaseOffset = curOffset;

                if (reader.ReadInt32() != _curEntry)
                    throw new InvalidDataException(TextResources.InvalidDataMauZ);
                long index = reader.ReadInt64();

                if (entries[index] != null)
                    throw new InvalidDataException(TextResources.InvalidDataMauZ);

                string path = DieFledermausStream.GetString(reader, ref curOffset, true);

                curOffset += (sizeof(int) + sizeof(long));

                entries[index] = LoadMausStream(reader.BaseStream, path, true, index, curBaseOffset, ref curOffset);
            }

            long metaOffset = curOffset;
            //All Offsets
            if (reader.ReadInt32() != _allOffsets)
                throw new InvalidDataException(TextResources.InvalidDataMauZ);
            //Size of head.
            curOffset += sizeof(int);

            HashSet<long> indices = new HashSet<long>();

            for (long i = 0; i < entryCount; i++)
            {
                if (reader.ReadInt32() != _curOffset)
                    throw new InvalidDataException(TextResources.InvalidDataMauZ);
                //Size of curOffset + size of ID + size of offset
                const long offsetSize = sizeof(int) + sizeof(long) + sizeof(long);

                curOffset += offsetSize;

                long index = reader.ReadInt64();
                if (index < 0 || index >= entryCount || !indices.Add(index))
                    throw new InvalidDataException(TextResources.InvalidDataMauZ);

                string basePath = entries[index].Path;

                string curPath = DieFledermausStream.GetString(reader, ref curOffset, true);
                if (curPath == "//V" + index.ToString(NumberFormatInfo.InvariantInfo))
                    curPath = null;

                if (!string.Equals(curPath, basePath, StringComparison.Ordinal))
                    throw new InvalidDataException(TextResources.InvalidDataMauZ);

                if (reader.ReadInt64() != entries[index].Offset)
                    throw new InvalidDataException(TextResources.InvalidDataMauZ);
            }

            if (reader.ReadInt64() != metaOffset)
                throw new InvalidDataException(TextResources.InvalidDataMauZ);

            _entries.AddRange(entries);
        }

        internal DieFledermauZItem LoadMausStream(Stream _baseStream, string path, bool readMagNum, long index, long baseOffset, ref long curOffset)
        {
            if (path == "//V" + index.ToString(NumberFormatInfo.InvariantInfo))
                path = null;
            else if (index >= 0 && !DieFledermausStream.IsValidFilename(path, false, DieFledermausStream.AllowDirNames.Unknown, nameof(path)))
                throw new InvalidDataException(TextResources.InvalidDataMauZ);

            DieFledermausStream mausStream;

            try
            {
                mausStream = new DieFledermausStream(_baseStream, readMagNum, path);
            }
            catch (InvalidDataException e)
            {
                throw new InvalidDataException(TextResources.InvalidDataMauZ, e);
            }
            catch (NotSupportedException e)
            {
                throw new InvalidDataException(TextResources.InvalidDataMauZ, e);
            }

            long headLength = mausStream.HeadLength;

            DieFledermauZItem returner;

            bool notDir = true;

            if (index < 0)
            {
                path = mausStream.Filename;
            }
            else if (mausStream.Filename == null)
            {
                if (mausStream.EncryptionFormat == MausEncryptionFormat.None)
                    throw new InvalidDataException(TextResources.InvalidDataMauZ);
            }
            else if (path == null)
            {
                path = mausStream.Filename;
            }
            else if (!path.Equals(mausStream.Filename, StringComparison.Ordinal))
                throw new InvalidDataException(TextResources.InvalidDataMauZ);

            if (path == null)
            {
                if (index < 0 || mausStream.CompressedLength > (mausStream.LegalKeySizes.MaxSize >> 3) + (mausStream.BlockByteCount * 3) +
                    DieFledermausStream.Max8Bit + DieFledermausStream.Max16Bit || DieFledermauZEmptyDirectory.HasNonDirValues(mausStream))
                {
                    returner = new DieFledermauZArchiveEntry(this, path, mausStream, baseOffset, curOffset);
                }
                else returner = new DieFledermauZItemUnknown(this, mausStream, baseOffset, curOffset);
            }
            else
            {
                string regPath;
                int end = path.Length - 1;
                if (path[end] == '/')
                {
                    returner = new DieFledermauZEmptyDirectory(this, path, mausStream, baseOffset, curOffset);
                    if (mausStream.EncryptionFormat == MausEncryptionFormat.None)
                        DieFledermauZEmptyDirectory.CheckStream(mausStream);
                    regPath = path.Substring(0, end);
                    notDir = false;
                }
                else
                {
                    returner = new DieFledermauZArchiveEntry(this, path, mausStream, baseOffset, curOffset);
                    regPath = path;
                }

                PathSeparator pathSep = new PathSeparator(regPath);

                if (_entryDict.ContainsKey(path) || _entryDict.ContainsKey(regPath) ||
                    _entryDict.Keys.Any(pathSep.BeginsWith) || _entryDict.Keys.Any(pathSep.OtherBeginsWith))
                    throw new InvalidDataException(TextResources.InvalidDataMauZ);
                _entryDict.Add(path, (int)index);
            }

            if (notDir)
            {
                if (_baseStream.CanSeek)
                    _baseStream.Seek(mausStream.CompressedLength, SeekOrigin.Current);
                else
                    mausStream.GetBuffer();
            }

            curOffset += mausStream.HeadLength + mausStream.CompressedLength;
            return returner;
        }

        private MausBufferStream _bufferStream;
        /// <summary>
        /// Decrypts the current instance.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// The current instance is disposed.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// <para>The current instance is in write-only mode.</para>
        /// <para>-OR-</para>
        /// <para>The stream contains unsupported options.</para>
        /// </exception>
        /// <exception cref="InvalidDataException">
        /// The stream contained invalid data.
        /// </exception>
        /// <exception cref="IOException">
        /// An I/O error occurred.
        /// </exception>
        /// <exception cref="CryptoException">
        /// The password is not correct. It is safe to attempt to call <see cref="Decrypt()"/>
        /// again if this exception is caught.
        /// </exception>
        public void Decrypt()
        {
            EnsureCanRead();
            _decryptData();
        }

        /// <summary>
        /// Gets a value indicating whether the current instance is in read-mode and has been successfully decrypted.
        /// </summary>
        public bool IsDecrypted { get { return _mode != MauZArchiveMode.Create && _headerGotten; } }

        private void _decryptData()
        {
            if (_headerGotten)
                return;

            OnProgress(MausProgressState.LoadingData);
            if (_bufferStream == null)
                _bufferStream = DieFledermausStream.GetBuffer(totalSize - curOffset, _baseStream);

            _bufferStream.Reset();

            if (_password == null && _key == null)
                throw new CryptoException(TextResources.KeyNotSetZ);

            if (_key == null)
            {
                OnProgress(MausProgressState.BuildingKey);
                _key = DieFledermausStream.GetKey(this);
            }

            using (MausBufferStream newBufferStream = DieFledermausStream.Decrypt(this, _key, _bufferStream))
            using (BinaryReader reader = new BinaryReader(newBufferStream))
            {
                ReadOptions(reader, true);
                long curOffset = newBufferStream.Position + sizeof(long) + sizeof(int); //Entry-count + "all entries"
                ReadDecrypted(reader, ref curOffset);
            }
            Array.Clear(_key, 0, _key.Length);
            OnProgress(MausProgressState.CompletedLoading);
        }

        private bool _gotHash;

        internal void ReadOptions(BinaryReader reader, bool fromEncrypted)
        {
            ushort optLen = reader.ReadUInt16();

            for (int i = 0; i < optLen; i++)
            {
                string curOption = DieFledermausStream.GetString(reader, ref curOffset, false);

                if (DieFledermausStream.ReadEncFormat(curOption, reader, optLen, ref i, ref curOffset, ref _encFmt, ref _keySizes, ref _keySize, ref _blockByteCount))
                {
                    if (!fromEncrypted && _encryptedOptions == null)
                        _encryptedOptions = new SettableOptions(this);
                    continue;
                }

                if (curOption.Equals(DieFledermausStream._kHash, StringComparison.Ordinal))
                {
                    CheckAdvance(optLen, ref i);
                    string newVal = DieFledermausStream.GetString(reader, ref curOffset, false);
                    MausHashFunction hashFunc;
                    if (!DieFledermausStream.HashDict.TryGetValue(newVal, out hashFunc))
                        throw new NotSupportedException(TextResources.FormatUnknownZ);

                    if (_gotHash || fromEncrypted)
                    {
                        if (hashFunc != _hashFunc)
                            throw new InvalidDataException(TextResources.FormatBadZ);
                    }
                    else
                    {
                        _hashFunc = hashFunc;
                        _gotHash = true;
                    }

                    continue;
                }

                if (curOption.Equals(DieFledermausStream._kComment, StringComparison.Ordinal))
                {
                    CheckAdvance(optLen, ref i);

                    byte[] comBytes = DieFledermausStream.GetStringBytes(reader, ref curOffset, false);

                    if (_comBytes == null)
                    {
                        if (fromEncrypted)
                            _encryptedOptions.InternalAdd(MauZOptionToEncrypt.Comment);
                        _comBytes = comBytes;
                        _comment = DieFledermausStream._textEncoding.GetString(comBytes);
                    }
                    else if (!DieFledermausStream.CompareBytes(comBytes, _comBytes))
                        throw new InvalidDataException(TextResources.FormatBadZ);

                    continue;
                }

                throw new NotSupportedException(TextResources.FormatUnknownZ);
            }
        }

        private static void CheckAdvance(int optLen, ref int i)
        {
            if (++i >= optLen)
                throw new InvalidDataException(TextResources.FormatBadZ);
        }

        internal void Delete(DieFledermauZItem item)
        {
            int index = _entries.IndexOf(item);
            _entries.RemoveAt(index);
            if (item.Path != null)
                _entryDict.Remove(item.Path);
            for (int i = index; i < _entries.Count; i++)
                _entryDict[_entries[i].Path] = i;
        }

        private byte[] _hmacExpected;
        /// <summary>
        /// Gets the loaded HMAC of the current instance, or <c>null</c> if the current instance is in write-mode or is not encrypted.
        /// </summary>
        public byte[] HMAC
        {
            get
            {
                if (_hmacExpected == null)
                    return null;
                return (byte[])_hmacExpected.Clone();
            }
        }

        private EntryList _entriesRO;
        /// <summary>
        /// Gets a collection containing all entries in the current archive, or <c>null</c> if the current instance is encrypted and has not yet been decrypted.
        /// </summary>
        public EntryList Entries { get { return _entriesRO; } }

        private readonly MauZArchiveMode _mode;
        /// <summary>
        /// Gets the mode of operation of the current instance.
        /// </summary>
        public MauZArchiveMode Mode { get { return _mode; } }

        private MausEncryptionFormat _encFmt;
        /// <summary>
        /// Gets the encryption format of the current instance.
        /// </summary>
        public MausEncryptionFormat EncryptionFormat { get { return _encFmt; } }

        private string _comment;
        /// <summary>
        /// Gets and sets the comment on the current instance.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// In a set operation, the current instance is disposed.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// In a set operation, the current instance is in read-only mode.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// In a set operation, the specified value is not <c>null</c>, and has a length which is equal to 0 or which is greater than 65536 UTF-8 bytes.
        /// </exception>
        public string Comment
        {
            get { return _comment; }
            set
            {
                EnsureCanWrite();
                _comBytes = DieFledermausStream.CheckComment(value);
                _comment = value;
            }
        }

        private byte[] _comBytes;
        /// <summary>
        /// Gets and sets a binary representation of a comment on the file.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// In a set operation, the current instance is closed.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// In a set operation, the current instance is in read-only mode.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// In a set operation, the specified value is not <c>null</c>, and has a length which is equal to 0 or which is greater than 65536.
        /// </exception>
        public byte[] CommentBytes
        {
            get
            {
                if (_comBytes == null) return null;
                return (byte[])_comBytes.Clone();
            }
            set
            {
                EnsureCanWrite();
                DieFledermausStream.CheckComment(value);
                if (value == null)
                {
                    _comment = null;
                    _comBytes = null;
                }
                else
                {
                    _comBytes = (byte[])value.Clone();
                    _comment = DieFledermausStream._textEncoding.GetString(value);
                }
            }
        }


        private SettableOptions _encryptedOptions;
        /// <summary>
        /// Gets a collection containing options which should be encrypted, or <c>null</c> if the current instance is not encrypted.
        /// </summary>
        public SettableOptions EncryptedOptions { get { return _encryptedOptions; } }

        private void _ensureCanSetKey()
        {
            if (_baseStream == null)
                throw new ObjectDisposedException(null, TextResources.CurrentClosed);
            if (_encFmt == MausEncryptionFormat.None)
                throw new NotSupportedException(TextResources.NotEncrypted);
            if (_mode == MauZArchiveMode.Read && _headerGotten)
                throw new InvalidOperationException(TextResources.AlreadyDecrypted);
        }

        private KeySizeList _keySizes;
        /// <summary>
        /// Gets a <see cref="KeySizeList"/> indicating all valid key sizes
        /// for the current encryption, or <c>null</c> if the current archive is not encrypted.
        /// </summary>
        public KeySizeList LegalKeySizes { get { return _keySizes; } }

        /// <summary>
        /// Gets the maximum number of bits in a single block of encrypted data, or 0 if the current instance is not encrypted.
        /// </summary>
        public int BlockSize { get { return _blockByteCount << 3; } }

        private int _blockByteCount;
        /// <summary>
        /// Gets the maximum number of bytes in a single block of encrypted data, or 0 if the current instance is not encrypted.
        /// </summary>
        public int BlockByteCount { get { return _blockByteCount; } }

        internal void EnsureCanWrite()
        {
            if (_baseStream == null)
                throw new ObjectDisposedException(null, TextResources.ArchiveClosed);
            if (_mode == MauZArchiveMode.Read)
                throw new NotSupportedException(TextResources.ArchiveReadMode);
        }

        internal void EnsureCanRead()
        {
            if (_baseStream == null)
                throw new ObjectDisposedException(null, TextResources.ArchiveClosed);
            if (_mode == MauZArchiveMode.Create)
                throw new NotSupportedException(TextResources.ArchiveWriteMode);
        }

        private int _pkCount;
        /// <summary>
        /// Gets and sets the number of PBKDF2 cycles used to generate the password, minus 9001.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// In a set operation, the current instance is disposed.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// <para>In a set operation, the current instance is not encrypted.</para>
        /// <para>-OR-</para>
        /// <para>In a set operation, the current instance is in read-only mode.</para>
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// In a set operation, the specified value is less than 0 or is greater than <see cref="int.MaxValue"/> minus 9001.
        /// </exception>
        public int PBKDF2CycleCount
        {
            get { return _pkCount; }
            set
            {
                EnsureCanWrite();
                if (_encFmt == MausEncryptionFormat.None)
                    throw new NotSupportedException(TextResources.NotEncrypted);
                if (value < 0 || value > DieFledermausStream.maxPkCount)
                    throw new ArgumentOutOfRangeException(nameof(value), value, string.Format(TextResources.OutOfRangeMinMax, 0, DieFledermausStream.maxPkCount));
                _pkCount = value;
            }
        }

        private byte[] _iv;
        /// <summary>
        /// Gets and sets the initialization vector used when encrypting the current instance.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// In a set operation, the current archive is disposed.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// <para>In a set operation, the current archive is in read-mode.</para>
        /// <para>-OR-</para>
        /// <para>In a set operation, the current archive is not encrypted.</para>
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// In a set operation, the specified value is <c>null</c>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// In a set operation, the length of the specified value is not equal to <see cref="BlockByteCount"/>.
        /// </exception>
        public byte[] IV
        {
            get
            {
                if (_iv == null) return null;
                return (byte[])_iv.Clone();
            }
            set
            {
                EnsureCanWrite();
                if (value == null) throw new ArgumentNullException(nameof(value));
                if (value.Length != _blockByteCount) throw new ArgumentException(TextResources.IvLength, nameof(value));
                _iv = (byte[])value.Clone();
            }
        }

        private byte[] _salt;
        /// <summary>
        /// Gets and sets the salt used to help obfuscate the key when setting the password.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// In a set operation, the current archive is disposed.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// <para>In a set operation, the current archive is in read-mode.</para>
        /// <para>-OR-</para>
        /// <para>In a set operation, the current archive is not encrypted.</para>
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// In a set operation, the specified value is <c>null</c>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// In a set operation, the length of the specified value is less than the maximum key length specified by <see cref="LegalKeySizes"/>.
        /// </exception>
        public byte[] Salt
        {
            get
            {
                if (_salt == null) return null;
                return (byte[])_salt.Clone();
            }
            set
            {
                EnsureCanWrite();

                if (value == null) throw new ArgumentNullException(nameof(value));
                if (value.Length < (_keySizes.MaxSize >> 3))
                    throw new ArgumentException(TextResources.SaltLength, nameof(value));

                _salt = (byte[])value.Clone();
            }
        }

        private int _keySize;
        /// <summary>
        /// Gets and sets the number of bits in the key.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// In a set operation, the current stream is closed.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// <para>In a set operation, the current instance is in read-only mode.</para>
        /// <para>-OR-</para>
        /// <para>In a set operation, the current instance is not encrypted.</para>
        /// <para>-OR-</para>
        /// <para>In a set operation, <see cref="Key"/> is not <c>null</c> and the specified value is not the proper length.</para>
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// In a set operation, the specified value is invalid according to <see cref="LegalKeySizes"/>.
        /// </exception>
        public int KeySize
        {
            get { return _keySize; }
            set
            {
                EnsureCanWrite();
                if (_encFmt == MausEncryptionFormat.None)
                    throw new NotSupportedException(TextResources.NotEncrypted);
                if (_key != null && value != _key.Length << 3)
                    throw new NotSupportedException(TextResources.NotSameLength);
                if (!IsValidKeyBitSize(value))
                    throw new ArgumentOutOfRangeException(nameof(value), value, TextResources.KeyLength);
                _keySize = value;
            }
        }

        private MausHashFunction _hashFunc;
        /// <summary>
        /// Gets and sets the hash function used by the current instance. The default is <see cref="MausHashFunction.Sha256"/>.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// In a set operation, the current instance is disposed.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// In a set operation, the current instance is in read-mode.
        /// </exception>
        /// <exception cref="InvalidEnumArgumentException">
        /// The specified value is not a valid <see cref="MausHashFunction"/> value.
        /// </exception>
        public MausHashFunction HashFunction
        {
            get { return _hashFunc; }
            set
            {
                EnsureCanWrite();
                if (DieFledermausStream.HashBDict.ContainsKey(value))
                    _hashFunc = value;
                else
                    throw new InvalidEnumArgumentException(nameof(value), (int)value, typeof(MausHashFunction));
            }
        }

        private string _password;
        /// <summary>
        /// Gets and sets the password used by the current instance.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// The current instance is disposed.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// The current stream is not encrypted.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// In a set operation, the current instance is in read-mode and has already been successfully decrypted.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// In a set operation, the specified value is not <c>null</c> and has a length of 0.
        /// </exception>
        public string Password
        {
            get { return _password; }
            set
            {
                _ensureCanSetKey();
                if (value != null && value.Length == 0)
                    throw new ArgumentException(TextResources.PasswordZeroLength, nameof(value));
                _password = value;
            }
        }

        private byte[] _key;
        /// <summary>
        /// Gets and sets a binary key used to encrypt or decrypt the current instance.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// The current instance is disposed.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// The current instance is not encrypted.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// In a set operation, the current instance is in read-mode and has already been successfully decrypted.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// In a set operation, the specified value has an invalid length according to <see cref="LegalKeySizes"/>.
        /// </exception>
        public byte[] Key
        {
            get
            {
                if (_key == null) return null;
                return (byte[])_key.Clone();
            }
            set
            {
                _ensureCanSetKey();
                if (value == null)
                {
                    _key = value;
                    return;
                }
                int keyBitSize = value.Length << 3;

                if (value.Length > int.MaxValue >> 3 || !_keySizes.Contains(keyBitSize))
                    throw new ArgumentException(TextResources.KeyLength, nameof(value));
                else
                {
                    _key = (byte[])value.Clone();
                    _keySize = keyBitSize;
                }
            }
        }

        /// <summary>
        /// Sets <see cref="Key"/> to a value derived from <see cref="Password"/>.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// The current instance is disposed.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// The current instance is not encrypted.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// <para>In a set operation, the current instance is in read-mode and has already been successfully decrypted.</para>
        /// <para>-OR-</para>
        /// <para><see cref="Password"/> is <c>null</c>.</para>
        /// </exception>
        public void DeriveKey()
        {
            _ensureCanSetKey();
            if (_password == null)
                throw new InvalidOperationException(TextResources.PasswordNotSet);
            _key = DieFledermausStream.GetKey(this);
        }

        #region Create
        /// <summary>
        /// Adds a new <see cref="DieFledermauZArchiveEntry"/> to the current archive using the specified compression format and encryption format.
        /// </summary>
        /// <param name="path">The path to the entry within the archive's file structure.</param>
        /// <param name="compressionFormat">The compression format of the archive entry.</param>
        /// <param name="encryptionFormat">The encryption format of the archive entry.</param>
        /// <returns>The newly-created <see cref="DieFledermauZArchiveEntry"/> object.</returns>
        /// <exception cref="ObjectDisposedException">
        /// The current instance is disposed.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// The current instance is in read-only mode.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="path"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="InvalidEnumArgumentException">
        /// <para><paramref name="compressionFormat"/> is not a valid <see cref="MausCompressionFormat"/> value.</para>
        /// <para>-OR-</para>
        /// <para><paramref name="encryptionFormat"/> is not a valid <see cref="MausEncryptionFormat"/> value.</para>
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <para><paramref name="path"/> is not a valid file path.</para>
        /// <para>-OR-</para>
        /// <para><paramref name="path"/> already exists.</para>
        /// </exception>
        public DieFledermauZArchiveEntry Create(string path, MausCompressionFormat compressionFormat, MausEncryptionFormat encryptionFormat)
        {
            EnsureCanWrite();
            DieFledermausStream.IsValidFilename(path, true, DieFledermausStream.AllowDirNames.Yes, nameof(path));
            ICompressionFormat compFormat;

            switch (compressionFormat)
            {
                case MausCompressionFormat.Deflate:
#if COMPLVL
                    compFormat = new DeflateCompressionFormat() { CompressionLevel = 0 };
#else
                    compFormat = new DeflateCompressionFormat();
#endif
                    break;
                case MausCompressionFormat.None:
                    compFormat = new NoneCompressionFormat();
                    break;
                case MausCompressionFormat.Lzma:
                    compFormat = new LzmaCompressionFormat() { DictionarySize = LzmaDictionarySize.Default };
                    break;
                default:
                    throw new InvalidEnumArgumentException(nameof(compressionFormat), (int)compressionFormat, typeof(MausCompressionFormat));
            }

            return Create(path, compFormat, encryptionFormat);
        }

        private DieFledermauZArchiveEntry Create(string path, ICompressionFormat compFormat, MausEncryptionFormat encryptionFormat)
        {
            switch (encryptionFormat)
            {
                case MausEncryptionFormat.Aes:
                case MausEncryptionFormat.Twofish:
                case MausEncryptionFormat.Threefish:
                case MausEncryptionFormat.None:
                    break;
                default:
                    throw new InvalidEnumArgumentException(nameof(encryptionFormat), (int)encryptionFormat, typeof(MausEncryptionFormat));
            }

            if (_entryDict.ContainsKey(path))
                throw new ArgumentException(TextResources.ArchiveExists, nameof(path));

            CheckSeparator(path, false);

            DieFledermauZArchiveEntry entry = new DieFledermauZArchiveEntry(this, path, compFormat, encryptionFormat);
            _entryDict.Add(path, _entries.Count);
            _entries.Add(entry);
            entry.HashFunction = _hashFunc;
            return entry;
        }

        /// <summary>
        /// Adds a new <see cref="DieFledermauZArchiveEntry"/> to the current archive using the specified compression format and no encryption.
        /// </summary>
        /// <param name="path">The path to the entry within the archive's file structure.</param>
        /// <param name="compressionFormat">The compression format of the archive entry.</param>
        /// <returns>The newly-created <see cref="DieFledermauZArchiveEntry"/> object.</returns>
        /// <exception cref="ObjectDisposedException">
        /// The current instance is disposed.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// The current instance is in read-only mode.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="path"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="InvalidEnumArgumentException">
        /// <paramref name="compressionFormat"/> is not a valid <see cref="MausCompressionFormat"/> value.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <para><paramref name="path"/> is not a valid file path.</para>
        /// <para>-OR-</para>
        /// <para><paramref name="path"/> already exists.</para>
        /// </exception>
        /// <remarks>
        /// If <paramref name="path"/> contains an existing empty directory as one of its subdirectories, this method will remove the existing
        /// (no-longer-)empty directory.
        /// </remarks>
        public DieFledermauZArchiveEntry Create(string path, MausCompressionFormat compressionFormat)
        {
            return Create(path, compressionFormat, 0);
        }

        /// <summary>
        /// Adds a new <see cref="DieFledermauZArchiveEntry"/> to the current archive using DEFLATE compression and the specified encryption format.
        /// </summary>
        /// <param name="path">The path to the entry within the archive's file structure.</param>
        /// <param name="encryptionFormat">The encryption format of the archive entry.</param>
        /// <returns>The newly-created <see cref="DieFledermauZArchiveEntry"/> object.</returns>
        /// <exception cref="ObjectDisposedException">
        /// The current instance is disposed.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// The current instance is in read-only mode.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="path"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="InvalidEnumArgumentException">
        /// <paramref name="encryptionFormat"/> is not a valid <see cref="MausEncryptionFormat"/> value.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <para><paramref name="path"/> is not a valid file path.</para>
        /// <para>-OR-</para>
        /// <para><paramref name="path"/> already exists.</para>
        /// </exception>
        /// <remarks>
        /// If <paramref name="path"/> contains an existing empty directory as one of its subdirectories, this method will remove the existing
        /// (no-longer-)empty directory.
        /// </remarks>
        public DieFledermauZArchiveEntry Create(string path, MausEncryptionFormat encryptionFormat)
        {
            return Create(path, MausCompressionFormat.Deflate, encryptionFormat);
        }

        /// <summary>
        /// Adds a new <see cref="DieFledermauZArchiveEntry"/> to the current archive using DEFLATE compressoin and no encryption.
        /// </summary>
        /// <param name="path">The path to the entry within the archive's file structure.</param>
        /// <returns>The newly-created <see cref="DieFledermauZArchiveEntry"/> object.</returns>
        /// <exception cref="ObjectDisposedException">
        /// The current instance is disposed.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// The current instance is in read-only mode.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="path"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <para><paramref name="path"/> is not a valid file path.</para>
        /// <para>-OR-</para>
        /// <para><paramref name="path"/> already exists.</para>
        /// </exception>
        /// <remarks>
        /// If <paramref name="path"/> contains an existing empty directory as one of its subdirectories, this method will remove the existing
        /// (no-longer-)empty directory.
        /// </remarks>
        public DieFledermauZArchiveEntry Create(string path)
        {
            return Create(path, MausCompressionFormat.Deflate, 0);
        }

#if COMPLVL
        /// <summary>
        /// Adds a new <see cref="DieFledermauZArchiveEntry"/> to the current archive using DEFLATE compression
        /// and the specified encryption format.
        /// </summary>
        /// <param name="path">The path to the entry within the archive's file structure.</param>
        /// <param name="compressionLevel">The compression level of the DEFLATE compression.</param>
        /// <param name="encryptionFormat">The encryption format of the archive entry.</param>
        /// <returns>The newly-created <see cref="DieFledermauZArchiveEntry"/> object.</returns>
        /// <exception cref="ObjectDisposedException">
        /// The current instance is disposed.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// The current instance is in read-only mode.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="path"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="InvalidEnumArgumentException">
        /// <para><paramref name="compressionLevel"/> is not a valid <see cref="CompressionLevel"/> value.</para>
        /// <para>-OR-</para>
        /// <para><paramref name="encryptionFormat"/> is not a valid <see cref="MausEncryptionFormat"/> value.</para>
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <para><paramref name="path"/> is not a valid file path.</para>
        /// <para>-OR-</para>
        /// <para><paramref name="path"/> already exists.</para>
        /// </exception>
        /// <remarks>
        /// If <paramref name="path"/> contains an existing empty directory as one of its subdirectories, this method will remove the existing
        /// (no-longer-)empty directory.
        /// </remarks>
        public DieFledermauZArchiveEntry Create(string path, CompressionLevel compressionLevel, MausEncryptionFormat encryptionFormat)
        {
            EnsureCanWrite();
            DieFledermausStream.IsValidFilename(path, true, DieFledermausStream.AllowDirNames.Yes, nameof(path));

            DeflateCompressionFormat compFormat = new DeflateCompressionFormat() { CompressionLevel = compressionLevel };

            return Create(path, compFormat, encryptionFormat);
        }

        /// <summary>
        /// Adds a new <see cref="DieFledermauZArchiveEntry"/> to the current archive using DEFLATE compression.
        /// </summary>
        /// <param name="path">The path to the entry within the archive's file structure.</param>
        /// <param name="compressionLevel">The compression level of the DEFLATE compression.</param>
        /// <returns>The newly-created <see cref="DieFledermauZArchiveEntry"/> object.</returns>
        /// <exception cref="ObjectDisposedException">
        /// The current instance is disposed.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// The current instance is in read-only mode.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="path"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="InvalidEnumArgumentException">
        /// <paramref name="compressionLevel"/> is not a valid <see cref="CompressionLevel"/> value.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <para><paramref name="path"/> is not a valid file path.</para>
        /// <para>-OR-</para>
        /// <para><paramref name="path"/> already exists.</para>
        /// </exception>
        /// <remarks>
        /// If <paramref name="path"/> contains an existing empty directory as one of its subdirectories, this method will remove the existing
        /// (no-longer-)empty directory.
        /// </remarks>
        public DieFledermauZArchiveEntry Create(string path, CompressionLevel compressionLevel)
        {
            return Create(path, compressionLevel, MausEncryptionFormat.None);
        }
#endif

        /// <summary>
        /// Adds a new <see cref="DieFledermauZArchiveEntry"/> to the current archive using LZMA compression
        /// and the specified encryption format.
        /// </summary>
        /// <param name="path">The path to the entry within the archive's file structure.</param>
        /// <param name="dictionarySize">Options for the LZMA dictionary size.</param>
        /// <param name="encryptionFormat">The encryption format of the archive entry.</param>
        /// <returns>The newly-created <see cref="DieFledermauZArchiveEntry"/> object.</returns>
        /// <exception cref="ObjectDisposedException">
        /// The current instance is disposed.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// The current instance is in read-only mode.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="path"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="dictionarySize"/> is not <see cref="LzmaDictionarySize.Default"/>, and is an integer value less than
        /// <see cref="LzmaDictionarySize.MinValue"/> or greater than <see cref="LzmaDictionarySize.MaxValue"/>.
        /// </exception>
        /// <exception cref="InvalidEnumArgumentException">
        /// <paramref name="encryptionFormat"/> is not a valid <see cref="MausEncryptionFormat"/> value.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <para><paramref name="path"/> is not a valid file path.</para>
        /// <para>-OR-</para>
        /// <para><paramref name="path"/> already exists.</para>
        /// </exception>
        /// <remarks>
        /// If <paramref name="path"/> contains an existing empty directory as one of its subdirectories, this method will remove the existing
        /// (no-longer-)empty directory.
        /// </remarks>
        public DieFledermauZArchiveEntry Create(string path, LzmaDictionarySize dictionarySize, MausEncryptionFormat encryptionFormat)
        {
            EnsureCanWrite();
            DieFledermausStream.IsValidFilename(path, true, DieFledermausStream.AllowDirNames.Yes, nameof(path));

            if (dictionarySize != LzmaDictionarySize.Default)
                dictionarySize = LzmaDictionarySize.Size8m;
            else if (dictionarySize < LzmaDictionarySize.MinValue || dictionarySize > LzmaDictionarySize.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(dictionarySize), dictionarySize, TextResources.OutOfRangeLzma);

            LzmaCompressionFormat compFormat = new LzmaCompressionFormat() { DictionarySize = dictionarySize };

            return Create(path, compFormat, encryptionFormat);
        }

        /// <summary>
        /// Adds a new <see cref="DieFledermauZArchiveEntry"/> to the current archive using LZMA compression
        /// and no encryption.
        /// </summary>
        /// <param name="path">The path to the entry within the archive's file structure.</param>
        /// <param name="dictionarySize">Options for the LZMA dictionary size.</param>
        /// <returns>The newly-created <see cref="DieFledermauZArchiveEntry"/> object.</returns>
        /// <exception cref="ObjectDisposedException">
        /// The current instance is disposed.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// The current instance is in read-only mode.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="path"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="dictionarySize"/> is not <see cref="LzmaDictionarySize.Default"/>, and is an integer value less than
        /// <see cref="LzmaDictionarySize.MinValue"/> or greater than <see cref="LzmaDictionarySize.MaxValue"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <para><paramref name="path"/> is not a valid file path.</para>
        /// <para>-OR-</para>
        /// <para><paramref name="path"/> already exists.</para>
        /// </exception>
        /// <remarks>
        /// If <paramref name="path"/> contains an existing empty directory as one of its subdirectories, this method will remove the existing
        /// (no-longer-)empty directory.
        /// </remarks>
        public DieFledermauZArchiveEntry Create(string path, LzmaDictionarySize dictionarySize)
        {
            return Create(path, dictionarySize, MausEncryptionFormat.None);
        }
        #endregion

        /// <summary>
        /// Adds a new empty directory to the current archive.
        /// </summary>
        /// <param name="path">The path to the empty directory within the archive's file structure.</param>
        /// <param name="encryptionFormat">The encryption format of the empty directory.</param>
        /// <returns>A newly-created <see cref="DieFledermauZEmptyDirectory"/> object.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="path"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="InvalidEnumArgumentException">
        /// <paramref name="encryptionFormat"/> is not a valid <see cref="MausEncryptionFormat"/> value.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <para><paramref name="path"/> is not a valid file path.</para>
        /// <para>-OR-</para>
        /// <para><paramref name="path"/> already exists.</para>
        /// </exception>
        /// <exception cref="ObjectDisposedException">
        /// The current instance is disposed.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// The current instance is in read-only mode.
        /// </exception>
        /// <remarks>
        /// If <paramref name="path"/> contains an existing empty directory as one of its subdirectories, this method will remove the existing
        /// (no-longer-)empty directory.
        /// </remarks>
        public DieFledermauZEmptyDirectory AddEmptyDirectory(string path, MausEncryptionFormat encryptionFormat)
        {
            EnsureCanWrite();

            IsValidEmptyDirectoryPath(path, true);
            string pathSlash;

            switch (encryptionFormat)
            {
                case MausEncryptionFormat.Aes:
                case MausEncryptionFormat.None:
                case MausEncryptionFormat.Twofish:
                case MausEncryptionFormat.Threefish:
                    break;
                default:
                    throw new InvalidEnumArgumentException(nameof(encryptionFormat), (int)encryptionFormat, typeof(MausEncryptionFormat));
            }

            int end = path.Length - 1;
            if (path[end] == '/')
            {
                pathSlash = path;
                path = path.Substring(0, end);
            }
            else pathSlash = path + '/';

            if (_entryDict.ContainsKey(path))
                throw new ArgumentException(TextResources.ArchiveExists, nameof(path));
            if (_entryDict.ContainsKey(pathSlash))
                throw new ArgumentException(TextResources.ArchiveExistsDir, nameof(path));

            CheckSeparator(path, true);

            DieFledermauZEmptyDirectory empty = new DieFledermauZEmptyDirectory(this, pathSlash, encryptionFormat);
            _entryDict.Add(pathSlash, _entries.Count);
            _entries.Add(empty);
            empty.HashFunction = _hashFunc;
            return empty;
        }

        /// <summary>
        /// Adds a new empty directory to the current archive.
        /// </summary>
        /// <param name="path">The path to the empty directory within the archive's file structure.</param>
        /// <returns>A newly-created <see cref="DieFledermauZEmptyDirectory"/> object.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="path"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <para><paramref name="path"/> is not a valid file path.</para>
        /// <para>-OR-</para>
        /// <para><paramref name="path"/> already exists.</para>
        /// </exception>
        /// <exception cref="ObjectDisposedException">
        /// The current instance is disposed.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// The current instance is in read-only mode.
        /// </exception>
        /// <remarks>
        /// If <paramref name="path"/> contains an existing empty directory as one of its subdirectories, this method will remove the existing
        /// (no-longer-)empty directory.
        /// </remarks>
        public DieFledermauZEmptyDirectory AddEmptyDirectory(string path)
        {
            return AddEmptyDirectory(path, MausEncryptionFormat.None);
        }

        private void CheckSeparator(string path, bool dir)
        {
            PathSeparator pathSep = new PathSeparator(path);

            if (_entryDict.Keys.Any(pathSep.OtherBeginsWith))
                throw new ArgumentException(dir ? TextResources.ArchivePathNonEmpty : TextResources.ArchivePathExistingDir, nameof(path));

            if (_entryDict.Keys.Where(i => !i.EndsWith("/")).Any(pathSep.BeginsWith))
                throw new ArgumentException(TextResources.ArchivePathExistingFileAsDir, nameof(path));

            var emptyDirs = _entries.Where(pathSep.BeginsWithEmptyDir).ToArray();

            for (int i = 0; i < emptyDirs.Length; i++)
                emptyDirs[i].Delete();
        }

        private class PathSeparator
        {
            private string _basePath;

            public PathSeparator(string basePath)
            {
                _basePath = basePath;
            }

            private static bool _beginsWith(string basePath, string other)
            {
                return other.StartsWith(basePath, StringComparison.Ordinal) && (basePath.Length == other.Length || other[basePath.Length] == '/');
            }

            public bool BeginsWith(string other)
            {
                return _beginsWith(_basePath, other);
            }

            public bool OtherBeginsWith(string other)
            {
                return _beginsWith(other, _basePath);
            }

            public bool BeginsWithEmptyDir(DieFledermauZItem item)
            {
                DieFledermauZEmptyDirectory emptyDir = item as DieFledermauZEmptyDirectory;

                if (emptyDir == null) return false;
                string path = emptyDir.Path;

                if (path == null) return false;

                int baseEnd = path.Length - 1;
                if (path[baseEnd] == '/')
                    path = path.Substring(0, baseEnd);

                return _beginsWith(path, _basePath);
            }
        }

        /// <summary>
        /// Determines whether the specified value is a valid length for a key, in bits.
        /// </summary>
        /// <param name="bitCount">The number of bits to test.</param>
        /// <returns><c>true</c> if <paramref name="bitCount"/> is a valid bit count according to <see cref="LegalKeySizes"/>;
        /// <c>false</c> if <paramref name="bitCount"/> is invalid, or if the current instance is not encrypted.</returns>
        public bool IsValidKeyBitSize(int bitCount)
        {
            if (_keySizes == null) return false;

            return _keySizes.Contains(bitCount);
        }

        /// <summary>
        /// Determines whether the specified value is a valid length for a key, in bytes.
        /// </summary>
        /// <param name="byteCount">The number of bytes to test.</param>
        /// <returns><c>true</c> if <paramref name="byteCount"/> is a valid byte count according to <see cref="LegalKeySizes"/>;
        /// <c>false</c> if <paramref name="byteCount"/> is invalid, or if the current instance is not encrypted.</returns>
        public bool IsValidKeyByteSize(int byteCount)
        {
            if (_keySizes == null || byteCount > int.MaxValue >> 3) return false;

            return _keySizes.Contains(byteCount << 3);
        }

        /// <summary>
        /// Determines if the specified value is a valid value for a file path.
        /// </summary>
        /// <param name="path">The value to test.</param>
        /// <returns><c>true</c> if <paramref name="path"/> is a valid path; <c>false</c> if an element in <paramref name="path"/> has a length of 0, has a length
        /// greater than 256 UTF-8 bytes, contains unpaired surrogate characters, contains non-whitespace control characters (non-whitespace characters
        /// between <c>\u0000</c> and <c>\u001f</c> inclusive, or between <c>\u007f</c> and <c>\u009f</c> inclusive), contains only whitespace,
        /// or is "." or ".." (the "current directory" and "parent directory" identifiers).</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="path"/> is <c>null</c>.
        /// </exception>
        public static bool IsValidFilePath(string path)
        {
            return DieFledermausStream.IsValidFilename(path, false, DieFledermausStream.AllowDirNames.Yes, nameof(path));
        }

        /// <summary>
        /// Determines if the specified value is a valid value for an empty directory path.
        /// </summary>
        /// <param name="path">The value to test.</param>
        /// <returns><c>true</c> if <paramref name="path"/> is a valid path; <c>false</c> if an element in <paramref name="path"/> has a length of 0, has a length
        /// greater than 256 UTF-8 bytes, contains unpaired surrogate characters, contains non-whitespace control characters (non-whitespace characters
        /// between <c>\u0000</c> and <c>\u001f</c> inclusive, or between <c>\u007f</c> and <c>\u009f</c> inclusive), contains only whitespace,
        /// or is "." or ".." (the "current directory" and "parent directory" identifiers).</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="path"/> is <c>null</c>.
        /// </exception>
        public static bool IsValidEmptyDirectoryPath(string path)
        {
            return IsValidEmptyDirectoryPath(path, false);
        }

        private static bool IsValidEmptyDirectoryPath(string path, bool throwOnInvalid)
        {
            if (path == null)
                throw new ArgumentNullException(nameof(path));

            return DieFledermausStream.IsValidFilename(path, throwOnInvalid, DieFledermausStream.AllowDirNames.EmptyDir, nameof(path));
        }

        /// <summary>
        /// Releases all resources used by the current instance.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_baseStream == null)
                return;
            try
            {
                if (!disposing)
                    return;

                try
                {
                    WriteFile();
                }
                finally
                {
                    if (!_leaveOpen)
                        _baseStream.Dispose();
                }
            }
            finally
            {
                _baseStream = null;
                for (int i = 0; i < _entries.Count; i++)
                    _entries[i].DoDelete(false);
            }
        }

        private const long BaseOffset = 28;

        private void WriteFile()
        {
            if (_mode == MauZArchiveMode.Read)
                return;

            if (_entries.Count == 0)
                throw new InvalidOperationException(TextResources.ArchiveEmpty);
            if (_encFmt != MausEncryptionFormat.None && _password == null)
                throw new InvalidOperationException(TextResources.KeyNotSetZ);

            long length = 16;


            if (_encFmt != MausEncryptionFormat.None && _key == null)
            {
                OnProgress(MausProgressState.BuildingKey);
                _key = DieFledermausStream.GetKey(this);
            }

            DieFledermauZItem[] entries = _entries.ToArray();
            MausBufferStream[] entryStreams = new MausBufferStream[entries.Length];
            byte[][] paths = new byte[entries.Length][];

            OnProgress(MausProgressState.ArchiveCompressingEntries);
            for (int i = 0; i < entries.Length; i++)
            {
                var curEntry = entries[i];

                MausBufferStream curStream = curEntry.GetWritten();
                entryStreams[i] = curStream;

                string path;
                if (curEntry.IsFilenameEncrypted)
                    path = "//V" + i.ToString(NumberFormatInfo.InvariantInfo);
                else
                    path = curEntry.Path;
                byte[] curPath = DieFledermausStream._textEncoding.GetBytes(path);
                paths[i] = curPath;
            }

            ByteOptionList options = new ByteOptionList();
            if (_encFmt != MausEncryptionFormat.None)
            {
                switch (_encFmt)
                {
                    case MausEncryptionFormat.Aes:
                        options.Add(DieFledermausStream._kEncAes);
                        break;
                    case MausEncryptionFormat.Twofish:
                        options.Add(DieFledermausStream._kEncTwofish);
                        break;
                    case MausEncryptionFormat.Threefish:
                        options.Add(DieFledermausStream._kEncThreefish);
                        break;
                }
                options.Add((short)_keySize);

                options.Add(DieFledermausStream._kHash);
                options.Add(DieFledermausStream.HashBDict[_hashFunc]);
            }

            if (_encryptedOptions == null || !_encryptedOptions.Contains(MauZOptionToEncrypt.Comment))
                AddComment(options);

            long curOffset = BaseOffset;
            AddSize(options, ref length, ref curOffset);

            ByteOptionList encryptedOptions;

            if (_encFmt == MausEncryptionFormat.None)
                encryptedOptions = null;
            else
            {
                encryptedOptions = new ByteOptionList();
                long keyByteLength = _keySize >> 3;
                long size = keyByteLength + sizeof(long);
                if (_encFmt == MausEncryptionFormat.Threefish)
                    size += keyByteLength;
                else
                    size += _blockByteCount;

                size += DieFledermausStream.GetHashLength(_hashFunc);

                length += size;
                curOffset += size;

                if (_encryptedOptions.Contains(MauZOptionToEncrypt.Comment))
                    AddComment(encryptedOptions);

                //TODO: Other encrypted options

                AddSize(encryptedOptions, ref length, ref curOffset);
            }

            using (MausBufferStream dataStream = new MausBufferStream())
            {
                OnProgress(MausProgressState.ArchiveBuildingEntries);
                byte[] hmac = null;
                if (_encFmt == MausEncryptionFormat.None)
                {
#if NOLEAVEOPEN
                    BinaryWriter dataWriter = new BinaryWriter(dataStream);
#else
                    using (BinaryWriter dataWriter = new BinaryWriter(dataStream, DieFledermausStream._textEncoding, true))
#endif
                    {
                        WriteFiles(entries, entryStreams, paths, dataWriter, curOffset);
                    }
                }
                else
                {
                    using (MausBufferStream cryptStream = new MausBufferStream())
                    {
#if NOLEAVEOPEN
                        BinaryWriter cryptWriter = new BinaryWriter(cryptStream);
#else
                        using (BinaryWriter cryptWriter = new BinaryWriter(cryptStream, DieFledermausStream._textEncoding, true))
#endif
                        {
                            encryptedOptions.Write(cryptWriter);
                            WriteFiles(entries, entryStreams, paths, cryptWriter, cryptStream.Position
                                + sizeof(int) + sizeof(long)); //Size of "all-entries" + size of entry count
                        }

                        cryptStream.Reset();

                        hmac = DieFledermausStream.Encrypt(this, dataStream, cryptStream, _key);
                    }
                }
                dataStream.Reset();
                length += dataStream.Length;

#if NOLEAVEOPEN
                BinaryWriter writer = new BinaryWriter(_baseStream);
#else
                using (BinaryWriter writer = new BinaryWriter(_baseStream, DieFledermausStream._textEncoding, true))
#endif
                {
                    OnProgress(MausProgressState.WritingHead);
                    writer.Write(_mHead);
                    writer.Write(_versionShort);

                    writer.Write(length);

                    options.Write(writer);

                    if (_encFmt != MausEncryptionFormat.None)
                    {
                        writer.Write((long)_pkCount);
                        writer.Write(hmac);
                        writer.Write(_salt, 0, _key.Length);
                        writer.Write(_iv);
                    }
                    dataStream.BufferCopyTo(_baseStream, false);
                }
#if NOLEAVEOPEN
                writer.Flush();
#endif
            }
            OnProgress(MausProgressState.CompletedWriting);
        }

        private void AddComment(ByteOptionList options)
        {
            if (!string.IsNullOrEmpty(_comment))
            {
                options.Add(DieFledermausStream._kComment);
                options.Add(_comBytes);
            }
        }

        private static void AddSize(ByteOptionList options, ref long length, ref long curOffset)
        {
            long size = options.GetSize();
            length += size;
            curOffset += size;
        }

        private static void WriteFiles(DieFledermauZItem[] entries, MausBufferStream[] entryStreams, byte[][] paths, BinaryWriter writer, long curOffset)
        {
            writer.Write(entries.LongLength);

            writer.Write(_allEntries);

            long[] offsets = new long[entries.Length];

            for (long i = 0; i < entries.LongLength; i++)
            {
                var curStream = entryStreams[i];
                offsets[i] = curOffset;
                writer.Write(_curEntry);
                writer.Write(i);

                byte[] pathBytes = paths[i];

                writer.Write((byte)pathBytes.Length);
                writer.Write(pathBytes);

                curStream.BufferCopyTo(writer.BaseStream, true);

                curOffset += 13L + pathBytes.Length + curStream.Length;

                curStream.Dispose();
            }

            writer.Write(_allOffsets);

            for (long i = 0; i < entries.LongLength; i++)
            {
                byte[] pathBytes = paths[i];
                writer.Write(_curOffset);
                writer.Write(i);
                writer.Write((byte)pathBytes.Length);
                writer.Write(pathBytes);
                writer.Write(offsets[i]);
            }

            writer.Write(curOffset);
        }

        /// <summary>
        /// Destructor.
        /// </summary>
        ~DieFledermauZArchive()
        {
            Dispose(false);
        }

        internal void AddPath(string path, DieFledermauZItem item)
        {
            _entryDict.Add(path, _entries.IndexOf(item));
        }

        /// <summary>
        /// Raised when the current archive is reading or writing data, and the progress changes meaningfully.
        /// </summary>
        public event MausProgressEventHandler Progress;

        private void OnProgress(MausProgressState state)
        {
            OnProgress(new MausProgressEventArgs(state));
        }

        void IMausProgress.OnProgress(MausProgressState state)
        {
            OnProgress(new MausProgressEventArgs(state));
        }

        private void OnProgress(MausProgressEventArgs e)
        {
            if (Progress != null)
                Progress(this, e);
        }

        void IMausProgress.OnProgress(MausProgressEventArgs e)
        {
            OnProgress(e);
        }

        void ICodeProgress.SetProgress(long inSize, long outSize)
        {
            MausProgressState state;
            if (_mode == MauZArchiveMode.Create)
                state = MausProgressState.CompressingWithSize;
            else
                state = MausProgressState.DecompressingWithSize;

            if (Progress != null)
                Progress(this, new MausProgressEventArgs(state, inSize, outSize));
        }

        /// <summary>
        /// Represents a list of <see cref="DieFledermauZItem"/> objects.
        /// </summary>
        [DebuggerDisplay(DieFledermausStream.CollectionDebuggerDisplay)]
        [DebuggerTypeProxy(typeof(DebugView))]
        public class EntryList : IList<DieFledermauZItem>, IList
#if IREADONLY
            , IReadOnlyList<DieFledermauZItem>
#endif
        {
            private DieFledermauZArchive _archive;

            internal EntryList(DieFledermauZArchive archive)
            {
                _archive = archive;
                _paths = new PathCollection(this);
            }

            /// <summary>
            /// Get the element at the specified index.
            /// </summary>
            /// <param name="index">The index of the element to get.</param>
            /// <exception cref="ArgumentOutOfRangeException">
            /// <paramref name="index"/> is less than 0 or is greater than or equal to <see cref="Count"/>.
            /// </exception>
            public DieFledermauZItem this[int index]
            {
                get { return _archive._entries[index]; }
            }

            internal void ReplaceElement(int index, DieFledermauZItem value)
            {
                _archive._entries[index] = value;
            }

            DieFledermauZItem IList<DieFledermauZItem>.this[int index]
            {
                get { return _archive._entries[index]; }
                set { throw new NotSupportedException(TextResources.CollectReadOnly); }
            }

            object IList.this[int index]
            {
                get { return _archive._entries[index]; }
                set { throw new NotSupportedException(TextResources.CollectReadOnly); }
            }

            /// <summary>
            /// Gets the number of elements in the list.
            /// </summary>
            public int Count { get { return _archive._entries.Count; } }

            private PathCollection _paths;
            /// <summary>
            /// Gets a collection containing all filenames and directory names in the current instance.
            /// </summary>
            public PathCollection Paths { get { return _paths; } }

            /// <summary>
            /// Gets the entry associated with the specified path.
            /// </summary>
            /// <param name="path">The path to search for in the archive.</param>
            /// <param name="value">When this method returns, contains the value associated with <paramref name="path"/>, or <c>null</c>
            /// if <paramref name="path"/> was not found. This parameter is passed uninitialized.
            /// </param>
            /// <returns><c>true</c> if <paramref name="path"/> was found; <c>false</c> otherwise.</returns>
            /// <exception cref="ArgumentNullException">
            /// <paramref name="path"/> is <c>null</c>.
            /// </exception>
            public bool TryGetEntry(string path, out DieFledermauZItem value)
            {
                if (path == null)
                    throw new ArgumentNullException(nameof(path));

                int index;
                if (_archive._entryDict.TryGetValue(path, out index) || _archive._entryDict.TryGetValue(path + "/", out index))
                {
                    value = this[index];
                    return true;
                }
                value = null;
                return false;
            }

            /// <summary>
            /// Gets a value indicating whether the current instance is entirely frozen against all further changes.
            /// </summary>
            public bool IsFrozen
            {
                get
                {
                    return _archive._baseStream == null || (_archive._mode == MauZArchiveMode.Read && _archive._headerGotten &&
                        !_archive._entries.Any(i => i is DieFledermauZItemUnknown));
                }
            }

            bool ICollection.IsSynchronized { get { return IsFrozen; } }

            object ICollection.SyncRoot { get { return ((IList)_archive._entries).SyncRoot; } }

            bool IList.IsFixedSize { get { return true; } }

            bool IList.IsReadOnly { get { return true; } }

            bool ICollection<DieFledermauZItem>.IsReadOnly { get { return true; } }

            /// <summary>
            /// Returns the index of the specified element.
            /// </summary>
            /// <param name="item">The element to search for in the list.</param>
            /// <returns>The index of <paramref name="item"/>, if found; otherwise, <c>null</c>.</returns>
            public int IndexOf(DieFledermauZItem item)
            {
                if (item == null || item.Archive != _archive) return -1;

                int dex;
                if (item.Path == null || !_archive._entryDict.TryGetValue(item.Path, out dex))
                    return _archive._entries.IndexOf(item);

                return dex;
            }

            int IList.IndexOf(object value)
            {
                return IndexOf(value as DieFledermauZItem);
            }

            /// <summary>
            /// Determines whether the specified element exists in the list.
            /// </summary>
            /// <param name="item">The element to search for in the list.</param>
            /// <returns><c>true</c> if <paramref name="item"/> was found; <c>false</c> otherwise.</returns>
            public bool Contains(DieFledermauZItem item)
            {
                return item != null && _archive._entries.Contains(item);
            }

            bool IList.Contains(object value)
            {
                return Contains(value as DieFledermauZItem);
            }

            /// <summary>
            /// Copies all elements in the collection to the specified array, starting at the specified index.
            /// </summary>
            /// <param name="array">The array to which the collection will be copied. The array must have zero-based indexing.</param>
            /// <param name="index">The index in <paramref name="array"/> at which copying begins.</param>
            /// <exception cref="ArgumentNullException">
            /// <paramref name="array"/> is <c>null</c>.
            /// </exception>
            /// <exception cref="ArgumentOutOfRangeException">
            /// <paramref name="index"/> is less than 0.
            /// </exception>
            /// <exception cref="ArgumentException">
            /// <paramref name="index"/> plus <see cref="Count"/> is greater than the length of <paramref name="array"/>.
            /// </exception>
            public void CopyTo(DieFledermauZItem[] array, int index)
            {
                _archive._entries.CopyTo(array, index);
            }

            void ICollection.CopyTo(Array array, int index)
            {
                ((ICollection)_archive._entries).CopyTo(array, index);
            }

            #region Not Supported
            void IList<DieFledermauZItem>.Insert(int index, DieFledermauZItem item)
            {
                throw new NotSupportedException(TextResources.CollectReadOnly);
            }

            void IList<DieFledermauZItem>.RemoveAt(int index)
            {
                throw new NotSupportedException(TextResources.CollectReadOnly);
            }

            void ICollection<DieFledermauZItem>.Add(DieFledermauZItem item)
            {
                throw new NotSupportedException(TextResources.CollectReadOnly);
            }

            bool ICollection<DieFledermauZItem>.Remove(DieFledermauZItem item)
            {
                throw new NotSupportedException(TextResources.CollectReadOnly);
            }

            void ICollection<DieFledermauZItem>.Clear()
            {
                throw new NotSupportedException(TextResources.CollectReadOnly);
            }

            int IList.Add(object value)
            {
                throw new NotSupportedException(TextResources.CollectReadOnly);
            }

            void IList.Clear()
            {
                throw new NotSupportedException(TextResources.CollectReadOnly);
            }

            void IList.Remove(object value)
            {
                throw new NotSupportedException(TextResources.CollectReadOnly);
            }

            void IList.Insert(int index, object value)
            {
                throw new NotSupportedException(TextResources.CollectReadOnly);
            }

            void IList.RemoveAt(int index)
            {
                throw new NotSupportedException(TextResources.CollectReadOnly);
            }
            #endregion

            /// <summary>
            /// Returns an enumerator which iterates through the collection.
            /// </summary>
            /// <returns>An enumerator which iterates through the collection.</returns>
            public Enumerator GetEnumerator()
            {
                return new Enumerator(this);
            }

            IEnumerator<DieFledermauZItem> IEnumerable<DieFledermauZItem>.GetEnumerator()
            {
                return GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }

            /// <summary>
            /// An enumerator which iterates through the collection.
            /// </summary>
            public struct Enumerator : IEnumerator<DieFledermauZItem>
            {
                private IEnumerator<DieFledermauZItem> _enum;

                internal Enumerator(EntryList list)
                {
                    _enum = list._archive._entries.GetEnumerator();
                }

                /// <summary>
                /// Gets the element at the current position in the enumerator.
                /// </summary>
                public DieFledermauZItem Current
                {
                    get { return _enum.Current; }
                }

                object IEnumerator.Current
                {
                    get { return _enum.Current; }
                }

                /// <summary>
                /// Disposes of the current instance.
                /// </summary>
                public void Dispose()
                {
                    if (_enum == null) return;
                    _enum.Dispose();
                    this = default(Enumerator);
                }

                /// <summary>
                /// Advances the enumerator to the next position in the collection.
                /// </summary>
                /// <returns><c>true</c> if the enumerator was successfully advanced; 
                /// <c>false</c> if the enumerator has passed the end of the collection.</returns>
                public bool MoveNext()
                {
                    if (_enum == null)
                        return false;
                    if (_enum.MoveNext())
                        return true;

                    Dispose();
                    return false;
                }

                void IEnumerator.Reset()
                {
                    _enum.Reset();
                }
            }

            private class DebugView
            {
                private EntryList _col;

                public DebugView(EntryList col)
                {
                    _col = col;
                }

                [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
                public DieFledermauZItem[] Items
                {
                    get { return _col.ToArray(); }
                }
            }

            /// <summary>
            /// A collection containing the paths of all entries in the collection.
            /// </summary>
            [DebuggerDisplay(DieFledermausStream.CollectionDebuggerDisplay)]
            [DebuggerTypeProxy(typeof(PathDebugView))]
            public class PathCollection : IList<string>, IList
#if IREADONLY
                , IReadOnlyList<string>
#endif
            {
                private EntryList _list;

                internal PathCollection(EntryList list)
                {
                    _list = list;
                }

                /// <summary>
                /// Gets the element at the specified index.
                /// </summary>
                /// <param name="index">The element at the specified index.</param>
                /// <exception cref="ArgumentOutOfRangeException">
                /// <paramref name="index"/> is less than 0 or is greater than or equal to <see cref="Count"/>.
                /// </exception>
                public string this[int index]
                {
                    get { return _list[index].Path; }
                }

                string IList<string>.this[int index]
                {
                    get { return _list[index].Path; }
                    set { throw new NotSupportedException(TextResources.CollectReadOnly); }
                }

                object IList.this[int index]
                {
                    get { return this[index]; }
                    set { throw new NotSupportedException(TextResources.CollectReadOnly); }
                }

                /// <summary>
                /// Gets the number of elements contained in the list.
                /// </summary>
                public int Count { get { return _list.Count; } }

                /// <summary>
                /// Gets a value indicating whether the current instance is entirely frozen against all further changes.
                /// </summary>
                public bool IsFrozen { get { return _list.IsFrozen; } }

                bool IList.IsFixedSize
                {
                    get { return true; }
                }

                bool IList.IsReadOnly
                {
                    get { return true; }
                }

                bool ICollection<string>.IsReadOnly
                {
                    get { return true; }
                }

                bool ICollection.IsSynchronized
                {
                    get { return _list.IsFrozen; }
                }

                object ICollection.SyncRoot
                {
                    get { return ((IList)_list).SyncRoot; }
                }

                /// <summary>
                /// Returns the index of the specified path.
                /// </summary>
                /// <param name="path">The path to search for in the list.</param>
                /// <returns>The index of <paramref name="path"/>, if found; otherwise, -1.</returns>
                public int IndexOf(string path)
                {
                    int dex;
                    if (path == null || !_list._archive._entryDict.TryGetValue(path, out dex))
                        return -1;
                    return dex;
                }

                int IList.IndexOf(object value)
                {
                    return IndexOf(value as string);
                }

                /// <summary>
                /// Gets a value indicating whether the specified path exists in the list.
                /// </summary>
                /// <param name="path">The path to search for in the list.</param>
                /// <returns><c>true</c> if <paramref name="path"/> was found; <c>false</c> otherwise.</returns>
                public bool Contains(string path)
                {
                    return IndexOf(path) >= 0;
                }

                bool IList.Contains(object value)
                {
                    return Contains(value as string);
                }

                /// <summary>
                /// Copies all elements in the collection to the specified array, starting at the specified index.
                /// </summary>
                /// <param name="array">The array to which the collection will be copied. The array must have zero-based indexing.</param>
                /// <param name="index">The index in <paramref name="array"/> at which copying begins.</param>
                /// <exception cref="ArgumentNullException">
                /// <paramref name="array"/> is <c>null</c>.
                /// </exception>
                /// <exception cref="ArgumentOutOfRangeException">
                /// <paramref name="index"/> is less than 0.
                /// </exception>
                /// <exception cref="ArgumentException">
                /// <paramref name="index"/> plus <see cref="Count"/> is greater than the length of <paramref name="array"/>.
                /// </exception>
                public void CopyTo(string[] array, int index)
                {
                    _list._archive._entryDict.Keys.CopyTo(array, index);
                }

                void ICollection.CopyTo(Array array, int index)
                {
                    ((ICollection)_list._archive._entryDict.Keys).CopyTo(array, index);
                }

                #region Not Supported
                int IList.Add(object value)
                {
                    throw new NotSupportedException(TextResources.CollectReadOnly);
                }

                void ICollection<string>.Add(string item)
                {
                    throw new NotSupportedException(TextResources.CollectReadOnly);
                }

                void IList.Clear()
                {
                    throw new NotSupportedException(TextResources.CollectReadOnly);
                }

                void ICollection<string>.Clear()
                {
                    throw new NotSupportedException(TextResources.CollectReadOnly);
                }

                void IList.Insert(int index, object value)
                {
                    throw new NotSupportedException(TextResources.CollectReadOnly);
                }

                void IList<string>.Insert(int index, string item)
                {
                    throw new NotSupportedException(TextResources.CollectReadOnly);
                }

                void IList.Remove(object value)
                {
                    throw new NotSupportedException(TextResources.CollectReadOnly);
                }

                bool ICollection<string>.Remove(string item)
                {
                    throw new NotSupportedException(TextResources.CollectReadOnly);
                }

                void IList.RemoveAt(int index)
                {
                    throw new NotSupportedException(TextResources.CollectReadOnly);
                }

                void IList<string>.RemoveAt(int index)
                {
                    throw new NotSupportedException(TextResources.CollectReadOnly);
                }
                #endregion

                /// <summary>
                /// Returns an enumerator which iterates through the collection.
                /// </summary>
                /// <returns>An enumerator which iterates through the collection.</returns>
                public Enumerator GetEnumerator()
                {
                    return new Enumerator(this);
                }

                IEnumerator<string> IEnumerable<string>.GetEnumerator()
                {
                    return GetEnumerator();
                }

                IEnumerator IEnumerable.GetEnumerator()
                {
                    return GetEnumerator();
                }

                /// <summary>
                /// An enumerator which iterates through the collection.
                /// </summary>
                public struct Enumerator : IEnumerator<string>
                {
                    private IEnumerator<string> _enum;

                    internal Enumerator(PathCollection keys)
                    {
                        _enum = keys._list._archive._entryDict.Keys.GetEnumerator();
                    }

                    /// <summary>
                    /// Gets the element at the current position in the enumerator.
                    /// </summary>
                    public string Current
                    {
                        get { return _enum.Current; }
                    }

                    object IEnumerator.Current
                    {
                        get { return _enum.Current; }
                    }

                    /// <summary>
                    /// Disposes of the current instance.
                    /// </summary>
                    public void Dispose()
                    {
                        if (_enum == null) return;
                        _enum.Dispose();
                        this = default(Enumerator);
                    }

                    /// <summary>
                    /// Advances the enumerator to the next position in the collection.
                    /// </summary>
                    /// <returns><c>true</c> if the enumerator was successfully advanced; 
                    /// <c>false</c> if the enumerator has passed the end of the collection.</returns>
                    public bool MoveNext()
                    {
                        if (_enum == null)
                            return false;
                        if (_enum.MoveNext())
                            return true;

                        Dispose();
                        return false;
                    }

                    void IEnumerator.Reset()
                    {
                        _enum.Reset();
                    }
                }

                private class PathDebugView
                {
                    private PathCollection _col;

                    public PathDebugView(PathCollection col)
                    {
                        _col = col;
                    }

                    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
                    public string[] Items
                    {
                        get { return _col.ToArray(); }
                    }
                }
            }
        }

        /// <summary>
        /// A collection of <see cref="MauZOptionToEncrypt"/> values.
        /// </summary>
        public class SettableOptions : MausSettableOptions<MauZOptionToEncrypt>
        {
            private DieFledermauZArchive _archive;

            internal SettableOptions(DieFledermauZArchive archive)
            {
                _archive = archive;
            }

            /// <summary>
            /// Gets a value indicating whether the current instance is read-only.
            /// Returns <c>true</c> if the underlying stream is closed or is in read-mode; <c>false</c> otherwise.
            /// </summary>
            /// <remarks>
            /// This property indicates that the collection cannot be changed externally. If <see cref="IsFrozen"/> is <c>false</c>,
            /// however, it may still be changed by the base stream.
            /// </remarks>
            public override bool IsReadOnly
            {
                get { return _archive._baseStream == null || _archive._mode == MauZArchiveMode.Read; }
            }

            /// <summary>
            /// Gets a value indicating whether the current instance is entirely frozen against all further changes.
            /// Returns <c>true</c> if the underlying stream is closed or is in read-mode and has successfully decoded the file;
            /// <c>false</c> otherwise.
            /// </summary>
            public override bool IsFrozen
            {
                get { return _archive._baseStream == null || (_archive._mode == MauZArchiveMode.Read && _archive._headerGotten); }
            }
        }
    }

    /// <summary>
    /// Indicates values to encrypt in a <see cref="DieFledermauZArchive"/>.
    /// </summary>
    public enum MauZOptionToEncrypt
    {
        /// <summary>
        /// Indicates that <see cref="DieFledermauZArchive.Comment"/> will be encrypted.
        /// </summary>
        Comment,
    }

    /// <summary>
    /// Indicates options for a <see cref="DieFledermauZArchive"/>.
    /// </summary>
    public enum MauZArchiveMode
    {
        /// <summary>
        /// The <see cref="DieFledermauZArchive"/> is in write-only mode.
        /// </summary>
        Create,
        /// <summary>
        /// The <see cref="DieFledermauZArchive"/> is in read-only mode.
        /// </summary>
        Read,
    }
}
