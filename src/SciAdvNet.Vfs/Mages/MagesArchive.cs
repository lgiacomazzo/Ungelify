﻿using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;

namespace SciAdvNet.Vfs.Mages
{
    public sealed class MagesArchive : IArchive
    {
        public const string MpkSignature = "MPK\0";
        private const int FirstEntryOffset = 0x40;
        private const int FileHeaderLength = 256;

        private readonly bool _leaveOpen;
        private bool _oldFormat;
        private short _versionMajor, _versionMinor;
        private int _v1LastReadId;

        private Func<BinaryReader, MpkFileEntry> _readEntryFunc;
        private Action<BinaryWriter, MpkFileEntry> _writeHeaderFunc;

        private MagesArchive(Stream stream, ArchiveMode mode, bool leaveOpen)
        {
            _leaveOpen = leaveOpen;
            ArchiveMode = mode;
            ArchiveStream = stream;
            ArchiveStream.Position = 0;
            ReadArchive();
        }

        public ImmutableArray<IFileEntry> Entries { get; private set; }
        public ArchiveMode ArchiveMode { get; }

        internal Stream ArchiveStream { get; }

        public static MagesArchive Load(Stream stream, ArchiveMode mode, bool leaveOpen = false)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }
            if (!stream.CanRead)
            {
                throw new ArgumentException("The specified stream must me readable.", nameof(stream));
            }
            if (mode == ArchiveMode.Update && !stream.CanWrite)
            {
                throw new ArgumentException("The specified stream must be writeable.", nameof(stream));
            }

            return new MagesArchive(stream, mode, leaveOpen);
        }

        public static MagesArchive Load(string path, ArchiveMode mode)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentNullException(nameof(path));
            }

            var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
            return new MagesArchive(stream, mode, leaveOpen: false);
        }

        public void RemoveEntry(IFileEntry entryToRemove)
        {
            //NOTA: Invocare questo metodo, e subito dopo chiudere l'archivio
            int indexEntry = Entries.IndexOf(entryToRemove);
            if (indexEntry == -1) {
                return;
            }
            // aggiorna tutti gli altri davanti all'elemento da rimuovere
            for (int index = indexEntry + 1; index < Entries.Length; ++index) 
            {
                MpkFileEntry entryToUpdate = (MpkFileEntry)Entries[index];
                entryToUpdate.FileHeaderOffset -= FileHeaderLength;
                entryToUpdate.setId(entryToUpdate.Id - 1);
            }
            // fine 
            Entries = Entries.Remove(entryToRemove);
        }

        public void AddEntry(FileStream reader, string name)
        {
            //aggiungo in coda all'array
            //nel file, l'header verrà messo dopo gli altri
            long fileHeaderOffset = FirstEntryOffset + Entries.Length * FileHeaderLength; //FirstEntryOffset + i * FileHeaderLength; per i in 0..Entries.Length - 1
            //non compresso
            var compressionMethod = (CompressionMethod)0;
            //l'ultimo elemento dell'array ha sempre l'id più alto
            int id = Entries.Length;
            //il nome è in codifica UTF-8
            string nameUtf8 = Encoding.UTF8.GetString(Encoding.ASCII.GetBytes(name));
            //ora si può creare
            var newEntry = new MpkFileEntry(this, id, name, 0, 0, 0, compressionMethod);
            //e non dimenticare di settare la posizione dell'header nel file
            newEntry.FileHeaderOffset = fileHeaderOffset;
            //infine, bisogna stabilire dove recuperare i byte da inserire nell'archivio, visto che non sono nell'archivio stesso
            MemoryStream ms = new MemoryStream();
            reader.CopyTo(ms);
            newEntry.UncompressedData = ms;
            //ultimo, ma più importante, aggiungere la nuova entry nell'array
            Entries = Entries.Add(newEntry);
        }

        public IFileEntry GetEntry(int id)
        {
            var entry = Entries.SingleOrDefault(x => x.Id == id);
            if (entry == null)
            {
                throw new ArgumentException(nameof(id));
            }

            return entry;
        }

        public IFileEntry GetEntry(string entryName)
        {
            var entry = Entries.SingleOrDefault(x => x.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                throw new ArgumentException(nameof(entryName));
            }

            return entry;
        }

        private void ReadArchive()
        {
            using (var reader = new BinaryReader(ArchiveStream, Encoding.UTF8, leaveOpen: true))
            {
                string signature = new string(reader.ReadChars(4));
                if (signature != MpkSignature)
                {
                    throw new InvalidDataException("The MPK format signature is not present in the specified stream.");
                }

                _versionMinor = reader.ReadInt16();
                _versionMajor = reader.ReadInt16();
                if (!(_versionMajor == 1 || _versionMajor == 2))
                {
                    throw new InvalidDataException("Unsupported format version.");
                }

                _oldFormat = _versionMajor == 1;
                _readEntryFunc = _oldFormat ? (Func<BinaryReader, MpkFileEntry>)ReadEntryV1 : ReadEntryV2;
                _writeHeaderFunc = _oldFormat ? (Action<BinaryWriter, MpkFileEntry>)WriteHeaderV1 : WriteHeaderV2;

                long entryCount = _oldFormat ? reader.ReadInt32() : reader.ReadInt64();
                var entries = ImmutableArray.CreateBuilder<IFileEntry>();
                for (int i = 0; i < entryCount; i++)
                {
                    ArchiveStream.Position = FirstEntryOffset + i * FileHeaderLength;
                    var entry = _readEntryFunc(reader);
                    entries.Add(entry);
                }

                Entries = entries.ToImmutable();
            }
        }

        private MpkFileEntry ReadEntryV1(BinaryReader reader)
        {
            long fileHeaderOffset = reader.BaseStream.Position;
            var compressionMethod = (CompressionMethod)reader.ReadInt32();
            int offset = reader.ReadInt32();
            long compressedLength = reader.ReadInt32();
            long uncompressedLength = reader.ReadInt32();

            ArchiveStream.Position += 16;
            string name = reader.ReadNullTerminatedString(Encoding.UTF8);
            var entry = new MpkFileEntry(this, _v1LastReadId++, name, offset, uncompressedLength, compressedLength, compressionMethod);
            entry.FileHeaderOffset = fileHeaderOffset;
            return entry;
        }

        private MpkFileEntry ReadEntryV2(BinaryReader reader)
        {
            long fileHeaderOffset = reader.BaseStream.Position;
            var compressionMethod = (CompressionMethod)reader.ReadInt32();
            int id = reader.ReadInt32();
            long offset = reader.ReadInt64();
            long compressedLength = reader.ReadInt64();
            long uncompressedLength = reader.ReadInt64();

            string name = reader.ReadNullTerminatedString(Encoding.UTF8);
            var entry = new MpkFileEntry(this, id, name, offset, uncompressedLength, compressedLength, compressionMethod);
            entry.FileHeaderOffset = fileHeaderOffset;
            return entry;
        }

        private void WriteArchive()
        {
            string tempFilePath = Path.GetTempFileName();
            var tempFile = File.Open(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite);
            using (var writer = new BinaryWriter(tempFile, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Encoding.UTF8.GetBytes(MpkSignature));
                writer.Write(_versionMinor);
                writer.Write(_versionMajor);
                writer.Write(_oldFormat ? Entries.Length : (long)Entries.Length);

                int padding = FirstEntryOffset - (int)tempFile.Position;
                writer.Write(new byte[padding]);

                int headerLength = Entries.Length * FileHeaderLength;
                writer.Write(new byte[headerLength]);

                for (int i = 0; i < Entries.Length; i++)
                {
                    var entry = Entries[i] as MpkFileEntry;
                    long dataOffset = tempFile.Position;

                    byte[] buffer = new byte[4096];
                    int read;
                    while ((read = entry.CompressedData.Read(buffer, 0, 4096)) != 0)
                    {
                        writer.Write(buffer, 0, read);
                    }

                    entry.DataOffset = dataOffset;
                    entry.Length = entry.UncompressedData.Length;
                    entry.CompressedLength = tempFile.Position - entry.DataOffset;

                    bool aligned = entry.CompressedLength % 2048 == 0 && entry.CompressedLength >= 2048;
                    if (!aligned)
                    {
                        int lengthWithPadding = (int)((entry.CompressedLength / 2048 + 1) * 2048);
                        padding = lengthWithPadding - (int)entry.CompressedLength;

                        if (i < Entries.Length - 1)
                        {
                            writer.Write(new byte[padding]);
                        }
                    }

                    long position = tempFile.Position;
                    tempFile.Position = entry.FileHeaderOffset;
                    _writeHeaderFunc(writer, entry);
                    tempFile.Position = position;

                    entry.CompressedData.Dispose();
                    entry.UncompressedData.Dispose();
                }
            }

            tempFile.Position = 0;
            ArchiveStream.SetLength(0);
            tempFile.CopyTo(ArchiveStream);
            tempFile.Dispose();

            File.Delete(tempFilePath);
        }

        private void WriteHeaderV1(BinaryWriter writer, MpkFileEntry entry)
        {
            writer.Write((int)entry.CompressionMethod);
            writer.Write(entry.Id);
            writer.Write((int)entry.DataOffset);
            writer.Write((int)entry.CompressedLength);
            writer.Write((int)entry.Length);
            writer.Write(new byte[16]);
            writer.Write(Encoding.UTF8.GetBytes(entry.Name));
        }

        private void WriteHeaderV2(BinaryWriter writer, MpkFileEntry entry)
        {
            writer.Write((int)entry.CompressionMethod);
            writer.Write(entry.Id);
            writer.Write(entry.DataOffset);
            writer.Write(entry.CompressedLength);
            writer.Write(entry.Length);
            writer.Write(Encoding.UTF8.GetBytes(entry.Name));
        }

        public void Dispose()
        {
            if (!_leaveOpen)
            {
                ArchiveStream.Dispose();
            }
        }

        public void SaveChanges()
        {
            WriteArchive();
        }

    }
}
