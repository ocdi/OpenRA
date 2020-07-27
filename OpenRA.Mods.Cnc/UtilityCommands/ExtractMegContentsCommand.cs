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
using System.IO;
using OpenRA.Mods.Cnc.FileSystem;

namespace OpenRA.Mods.Cnc.UtilityCommands
{
	class ExtractMegContentsCommand : IUtilityCommand
	{
		string IUtilityCommand.Name { get { return "--extract-meg"; } }

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			return args.Length == 3;
		}

		[Desc(@"D:\SteamLibrary\steamapps\common\CnCRemastered\Data\music.meg", @"D:\exported", "Extracts a meg file into the target directory")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			var source = args[1];
			var dest = args[2];

			if (!Directory.Exists(dest))
				Directory.CreateDirectory(dest);

			var package = new MegV3Loader.MegFile(File.OpenRead(source), source);

			foreach (var entry in package.Contents)
			{
				Console.WriteLine("{0}", entry);

				var baseDir = Path.Combine(dest, Path.GetDirectoryName(entry));
				if (!Directory.Exists(baseDir))
					Directory.CreateDirectory(baseDir);

				using (var destStream = File.OpenWrite(Path.Combine(dest, entry)))
				{
					var sourceStream = package.GetStream(entry);
					sourceStream.CopyTo(destStream);
				}
			}

			Console.WriteLine("All files extracted to {0}", dest);
		}
	}
}
