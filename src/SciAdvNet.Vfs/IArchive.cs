using System;
using System.Collections.Immutable;
using System.IO;

namespace SciAdvNet.Vfs
{
    public interface IArchive : IDisposable
    {
        ImmutableArray<IFileEntry> Entries { get; }
        ArchiveMode ArchiveMode { get; }

        IFileEntry GetEntry(int id);
        IFileEntry GetEntry(string name);
        void AddEntry(FileStream reader, string name);
        void RemoveEntry(IFileEntry entryToRemove);

        void SaveChanges();
    }
}
