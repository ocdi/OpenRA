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
	/// This class supports loading unencrypted V3 .meg files using
	/// reference documentation from here https://modtools.petrolution.net/docs/MegFileFormat
	/// </summary>
	public class MegV3Loader : IPackageLoader
	{
		public bool TryParsePackage(Stream s, string filename, OpenRA.FileSystem.FileSystem context, out IReadOnlyPackage package)
		{
			var position = s.Position;

			var id1 = s.ReadUInt32();
			var id2 = s.ReadUInt32();

			s.Position = position;

			if (id1 != HeaderId1 || id2 != HeaderId2)
			{
				package = null;
				return false;
			}

			package = new MegFile(s, filename);
			return true;
		}

		const int HeaderFileNameTableStartOffset = 0x18;

		const uint HeaderId1 = 0xffffffff;
		const uint HeaderId2 = 0x3F7D70A4;

		public sealed class MegFile : IReadOnlyPackage
		{
			readonly Stream s;

			readonly List<string> contents = new List<string>();

			readonly List<MegFileContentReference> fileData = new List<MegFileContentReference>();
			readonly string name;

			internal MegFile(FileStream s)
				: this(s, s.Name)
			{
			}

			public MegFile(Stream s, string filename)
			{
				this.s = s;
				name = filename;

				var id1 = s.ReadUInt32();
				var id2 = s.ReadUInt32();

				if (id1 != HeaderId1 || id2 != HeaderId2)
					throw new Exception("Invalid file signature for meg file");

				ParseMegHeader(s);
			}

			/// <summary>
			/// This method reads the file tables from a file. It is assumed the header magic bytes have already been read.
			/// </summary>
			/// <param name="reader">The reader of the stream</param>
			private void ParseMegHeader(Stream reader)
			{
				var dataStartOffset = reader.ReadUInt32();
				var numFileNames = reader.ReadUInt32();
				var numFiles = reader.ReadUInt32();
				var fileNameTableSize = reader.ReadUInt32();

				// The file names are an indexed array of strings
				for (var i = 0; i < numFileNames; i++)
				{
					var fileNameLength = reader.ReadUInt16();
					var fileNameBytes = reader.ReadBytes(fileNameLength);
					var fileName = Encoding.ASCII.GetString(fileNameBytes);

					contents.Add(fileName);
				}

				// The header indicates where we should be, so verify it
				if (reader.Position != fileNameTableSize + HeaderFileNameTableStartOffset)
					throw new Exception("File name table in .meg file inconsistent");

				// Now we load each file entry and associated info
				for (var i = 0; i < numFiles; i++)
				{
					var flags = reader.ReadUInt16();
					if (flags != 0)
						throw new Exception("Encrypted files are not supported or expected.");

					var crc = reader.ReadUInt32();
					reader.ReadUInt32(); // fileRecordIndex, which is unused
					var fileSize = reader.ReadUInt32();
					var fileStartOffset = reader.ReadUInt32();
					var fileNameIndex = reader.ReadUInt16();

					fileData.Add(new MegFileContentReference(crc, fileSize, fileStartOffset, fileNameIndex));
				}

				if (reader.Position != dataStartOffset)
					throw new Exception("Expected to be at data start offset");
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
				// Look up the index of the filename
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

		struct MegFileContentReference
		{
			public MegFileContentReference(uint crc32, uint fileSizeInBytes,
				uint fileStartOffsetInBytes, uint fileNameTableIndex)
			{
				Crc32 = crc32;
				FileSizeInBytes = fileSizeInBytes;
				FileStartOffsetInBytes = fileStartOffsetInBytes;
				FileNameTableRecordIndex = fileNameTableIndex;
			}

			public readonly uint Crc32;

			public readonly uint FileStartOffsetInBytes;

			public readonly uint FileSizeInBytes;

			public readonly uint FileNameTableRecordIndex;
		}
	}
}
