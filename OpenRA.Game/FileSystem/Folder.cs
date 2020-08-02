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

namespace OpenRA.FileSystem
{
	public sealed class Folder : IReadWritePackage
	{
		readonly string path;
		private IDictionary<string, string> linkCache = new Dictionary<string, string>();

		public Folder(string path)
		{
			this.path = path;
			if (!Directory.Exists(path))
				Directory.CreateDirectory(path);
		}

		public string Name { get { return path; } }

		public IEnumerable<string> Contents
		{
			get
			{
				foreach (var filename in Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly))
				{
					if (filename.EndsWith(".link"))
					{
						var rawFile = Path.GetFileName(filename.Substring(0, filename.Length - 5));
						if (ContainsLink(rawFile))
							yield return rawFile;
					}
					else
						yield return Path.GetFileName(filename);
				}

				foreach (var filename in Directory.GetDirectories(path))
					yield return Path.GetFileName(filename);
			}
		}

		public Stream GetStream(string filename)
		{
			try
			{
				// if we have not already determined the file path we can use the ContainsLink method that will
				// verify it exists and return true if the linked content exists
				var combined = linkCache.ContainsKey(filename) || ContainsLink(filename)
					? linkCache[filename]
					: Path.Combine(path, filename);

				return File.OpenRead(combined);
			}
			catch { return null; }
		}

		public bool Contains(string filename)
		{
			var combined = Path.Combine(path, filename);
			return (combined.StartsWith(path, StringComparison.Ordinal) && File.Exists(combined)) || ContainsLink(filename);
		}

		public bool ContainsLink(string filename)
		{
			var combined = Path.Combine(path, filename + ".link");

			if (!combined.StartsWith(path, StringComparison.Ordinal) || !File.Exists(combined))
				return false;

			var linkDest = File.ReadAllText(combined);

			if (File.Exists(linkDest))
			{
				linkCache[filename] = linkDest;
				return true;
			}

			return false;
		}

		public IReadOnlyPackage OpenPackage(string filename, FileSystem context)
		{
			var resolvedPath = Platform.ResolvePath(Path.Combine(Name, filename));
			if (Directory.Exists(resolvedPath))
				return new Folder(resolvedPath);

			// Zip files loaded from Folders (and *only* from Folders) can be read-write
			IReadWritePackage readWritePackage;
			if (ZipFileLoader.TryParseReadWritePackage(resolvedPath, out readWritePackage))
				return readWritePackage;

			// Other package types can be loaded normally
			IReadOnlyPackage package;
			var s = GetStream(filename);
			if (s == null)
				return null;

			if (context.TryParsePackage(s, filename, out package))
				return package;

			s.Dispose();
			return null;
		}

		public void Update(string filename, byte[] contents)
		{
			// HACK: ZipFiles can't be loaded as read-write from a stream, so we are
			// forced to bypass the parent package and load them with their full path
			// in FileSystem.OpenPackage.  Their internal name therefore contains the
			// full parent path too.  We need to be careful to not add a second path
			// prefix to these hacked packages.
			var filePath = filename.StartsWith(path) ? filename : Path.Combine(path, filename);

			Directory.CreateDirectory(Path.GetDirectoryName(filePath));
			using (var s = File.Create(filePath))
				s.Write(contents, 0, contents.Length);
		}

		public void Delete(string filename)
		{
			// HACK: ZipFiles can't be loaded as read-write from a stream, so we are
			// forced to bypass the parent package and load them with their full path
			// in FileSystem.OpenPackage.  Their internal name therefore contains the
			// full parent path too.  We need to be careful to not add a second path
			// prefix to these hacked packages.
			var filePath = filename.StartsWith(path) ? filename : Path.Combine(path, filename);
			if (Directory.Exists(filePath))
				Directory.Delete(filePath, true);
			else if (File.Exists(filePath))
				File.Delete(filePath);
		}

		public void Dispose() { }
	}
}
