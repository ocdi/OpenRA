#region Copyright & License Information
/*
 * Copyright 2007-2020 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenRA.FileSystem;
using OpenRA.Primitives;

namespace OpenRA.Mods.Cnc.FileSystem
{
	/// <summary>
	/// This class supports loading of the .meg V3 file format with the reference
	/// documentation here https://modtools.petrolution.net/docs/MegFileFormat
	/// There are 3 variants, and encrypted support that have not been implemented yet
	/// </summary>
	public class MegV3Loader : IPackageLoader
	{
		public bool TryParsePackage(Stream s, string filename, OpenRA.FileSystem.FileSystem context, out IReadOnlyPackage package)
		{
			var reader = new BinaryReader(s);
			var id1 = reader.ReadUInt32();
			var id2 = reader.ReadUInt32();

			if (!id1.Equals(HeaderId1) || !id2.Equals(HeaderId2))
			{
				package = null;
				return false;
			}

			package = new MegFile(s, filename);
			return true;
		}

		internal const int HeaderNumberOfFileNamesOffset = 0xC;
		internal const int HeaderNumberOfFilesOffset = 0x10;
		internal const int HeaderFileTableSizeOffset = 0x14;
		internal const int HeaderFileNameTableStartOffset = 0x18;

		internal const uint HeaderId1 = 0xffffffff;
		internal const uint HeaderId2 = 0x3F7D70A4;

		public sealed class MegFile : IReadOnlyPackage
		{
			private readonly Stream s;

			private readonly List<string> contents = new List<string>();

			private readonly List<MegFileContentReference> fileData = new List<MegFileContentReference>();
			private readonly string name;

			public MegFile(Stream s, string filename)
			{
				this.s = s;
				name = filename;

				ParseMegHeader(s);
			}

			private void ParseMegHeader(Stream s)
			{
				var reader = new BinaryReader(s);
				var id1 = reader.ReadUInt32();
				var id2 = reader.ReadUInt32();

				if (!id1.Equals(HeaderId1) || !id2.Equals(HeaderId2))
					throw new Exception("Invalid file signature for meg file");

				var dataStartOffset = reader.ReadUInt32();
				var numFileNames = reader.ReadUInt32();
				var numFiles = reader.ReadUInt32();
				var fileNameTableSize = reader.ReadUInt32();

				// the file names are an indexed array of strings
				for (uint i = 0; i < numFileNames; i++)
				{
					var fileNameLength = reader.ReadUInt16();
					var fileNameBytes = reader.ReadBytes(fileNameLength);
					var fileName = Encoding.ASCII.GetString(fileNameBytes);

					contents.Add(fileName);
				}

				// the header indicates where we should be, so verify it
				if (reader.BaseStream.Position != fileNameTableSize + HeaderFileNameTableStartOffset)
					throw new Exception("File name table in .meg file inconsistent");

				// now we load each file entry and associated info
				for (var i = 0; i < numFiles; i++)
				{
					var flags = reader.ReadUInt16();
					if (flags != 0) throw new Exception("Encrypted files are not supported or expected.");

					var crc = reader.ReadUInt32();
					var fileRecordIndex = reader.ReadUInt32();
					var fileSize = reader.ReadUInt32();
					var fileStartOffset = reader.ReadUInt32();
					var fileNameIndex = reader.ReadUInt16();

					fileData.Add(new MegFileContentReference(crc, fileRecordIndex, fileSize, fileStartOffset, fileNameIndex));
				}

				if (reader.BaseStream.Position != dataStartOffset)
				{
					throw new Exception("Expected to be at data start offset");
				}
			}

			public string Name { get { return name; } }

			public IEnumerable<string> Contents { get { return contents; } }

			public IReadOnlyDictionary<string, PackageEntry> Index
			{
				get
				{
					var absoluteIndex = Contents.ToDictionary(e => e, e =>
					{
						var index = contents.IndexOf(e);
						var reference = fileData.FirstOrDefault(a => a.FileNameTableRecordIndex == index);
						return new PackageEntry(reference.Crc32, reference.FileStartOffsetInBytes, reference.FileSizeInBytes);
					});
					return new ReadOnlyDictionary<string, PackageEntry>(absoluteIndex);
				}
			}

			public bool Contains(string filename)
			{
				return contents.Contains(filename);
			}

			public void Dispose()
			{
				s.Dispose();
			}

			public Stream GetStream(string filename)
			{
				// look up the index of the filename
				var index = contents.IndexOf(filename);
				var reference = fileData.FirstOrDefault(a => a.FileNameTableRecordIndex == index);

				return SegmentStream.CreateWithoutOwningStream(s, reference.FileStartOffsetInBytes, (int)reference.FileSizeInBytes);
			}

			public IReadOnlyPackage OpenPackage(string filename, OpenRA.FileSystem.FileSystem context)
			{
				IReadOnlyPackage package;
				var childStream = GetStream(filename);
				if (childStream == null)
					return null;

				if (context.TryParsePackage(childStream, filename, out package))
					return package;

				childStream.Dispose();
				return null;
			}
		}

		internal struct MegFileContentReference
		{
			public MegFileContentReference(uint crc32, uint fileTableRecordIndex, uint fileSizeInBytes,
				uint fileStartOffsetInBytes, uint fileNameTableIndex)
			{
				Crc32 = crc32;
				FileTableRecordIndex = fileTableRecordIndex;
				FileSizeInBytes = fileSizeInBytes;
				FileStartOffsetInBytes = fileStartOffsetInBytes;
				FileNameTableRecordIndex = fileNameTableIndex;
			}

			public uint Crc32;

			public uint FileStartOffsetInBytes;

			public uint FileSizeInBytes;

			public uint FileTableRecordIndex;

			public uint FileNameTableRecordIndex;
		}
	}
}
