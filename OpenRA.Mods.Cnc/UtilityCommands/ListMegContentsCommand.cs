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
using OpenRA.Mods.Cnc.FileFormats;
using OpenRA.Mods.Cnc.FileSystem;

namespace OpenRA.Mods.Cnc.UtilityCommands
{
	class ListMegContentsCommand : IUtilityCommand
	{
		string IUtilityCommand.Name { get { return "--list-meg"; } }

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			return args.Length == 2;
		}

		[Desc(@"D:\SteamLibrary\steamapps\common\CnCRemastered\Data\music.meg", "Lists the content ranges for a meg file")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			var package = new MegV3Loader.MegFile(File.OpenRead(args[1]), args[1]);
			foreach (var kv in package.Index)
			{
				Console.WriteLine("{0}:", kv.Key);
				Console.WriteLine("\tOffset: {0}", kv.Value.Offset);
				Console.WriteLine("\tLength: {0}", kv.Value.Length);
			}
		}
	}
}
