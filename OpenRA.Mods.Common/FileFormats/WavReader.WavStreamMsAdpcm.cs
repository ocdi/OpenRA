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
using System.Diagnostics;
using System.IO;
using OpenRA.Primitives;

namespace OpenRA.Mods.Common.FileFormats
{
	public static partial class WavReader
	{
		sealed class WavStreamMsAdpcm : ReadOnlyAdapterStream
		{
			/* format docs https://wiki.multimedia.cx/index.php/Microsoft_ADPCM
			 */

			const int MsADPCMAdaptCoeffCount = 7;

			internal static int[] AdaptationTable = new[]
			{
				230, 230, 230, 230, 307, 409, 512, 614,
				768, 614, 512, 409, 307, 230, 230, 230
			};

			internal static int[] AdaptCoeff1 = new[] { 256, 512, 0, 192, 240, 460, 392 };

			internal static int[] AdaptCoeff2 = new[] { 0, -256, 0, 64, 0, -208, -232 };

			readonly short channels;
			private readonly short blockAlign;
			private readonly short samplesperblock;
			readonly int blockDataSize;
			private readonly int numBlocks;

			int currentBlock;

			public WavStreamMsAdpcm(Stream stream, int dataSize, short blockAlign, short channels, int uncompressedSize, short samplesperblock)
				: base(stream)
			{
				this.channels = channels;
				this.blockAlign = blockAlign;
				this.samplesperblock = samplesperblock;
				blockDataSize = blockAlign - (channels * 4);
				numBlocks = dataSize / blockAlign;
			}

			protected override bool BufferData(Stream baseStream, Queue<byte> data)
			{
				var samples = new short[samplesperblock * channels];

				var empty = DecodeBlock(baseStream, samples);

				// buffer the samples
				foreach (var t in samples)
				{
					data.Enqueue((byte)t);
					data.Enqueue((byte)(t >> 8));
				}

				return empty;
			}

			/// <summary>
			/// Decodes a block of MS ADPCM data
			/// </summary>
			/// <param name="baseStream">The underlying stream to read data from</param>
			/// <param name="samples">A block worth of PCM samples</param>
			/// <returns>True when there is no more data or we can't continue</returns>
			bool DecodeBlock(Stream baseStream, short[] samples)
			{
				int chan, k, blockindx, sampleindx;
				short bytecode;
				short[] bpred = new short[2], chan_idelta = new short[2];

				int predict;
				int current;
				int idelta;

				var block = baseStream.ReadBytes(blockAlign);
				if (block.Length != blockAlign)
				{
					Debug.WriteLine(string.Format("Read insufficient bytes from the buffer, expected {0} but got {1}", blockAlign, block.Length));
					return true;
				}

				if (channels == 1)
				{
					bpred[0] = AssertPred(block[0]);

					// read a short from the bytes
					chan_idelta[0] = (short)(block[1] | (block[2]) << 8);
					chan_idelta[1] = 0;

					samples[1] = (short)(block[3] | (block[4] << 8));
					samples[0] = (short)(block[5] | (block[6] << 8));
					blockindx = 7;
				}
				else
				{
					bpred[0] = AssertPred(block[0]);
					bpred[1] = AssertPred(block[1]);

					chan_idelta[0] = (short)(block[2] | (block[3] << 8));
					chan_idelta[1] = (short)(block[4] | (block[5] << 8));

					samples[2] = (short)(block[6] | (block[7] << 8));
					samples[3] = (short)(block[8] | (block[9] << 8));

					samples[0] = (short)(block[10] | (block[11] << 8));
					samples[1] = (short)(block[12] | (block[13] << 8));

					blockindx = 14;
				}

				/*--------------------------------------------------------
	This was left over from a time when calculations were done
	as ints rather than shorts. Keep this around as a reminder
	in case I ever find a file which decodes incorrectly.
	if (chan_idelta [0] & 0x8000)
		chan_idelta [0] -= 0x10000 ;
	if (chan_idelta [1] & 0x8000)
		chan_idelta [1] -= 0x10000 ;
	--------------------------------------------------------*/

				/* Pull apart the packed 4 bit samples and store them in their
				** correct sample positions.
				*/

				sampleindx = 2 * channels;
				while (blockindx < blockDataSize)
				{
					bytecode = block[blockindx++];
					samples[sampleindx++] = (short)((bytecode >> 4) & 0x0F);
					samples[sampleindx++] = (short)(bytecode & 0x0F);
				}

				/* Decode the encoded 4 bit samples. */

				for (k = 2 * channels; k < (samplesperblock * channels); k++)
				{
					chan = (channels > 1) ? (k % 2) : 0;

					bytecode = (short)(samples[k] & 0xF);

					/* Compute next Adaptive Scale Factor (ASF) */
					idelta = chan_idelta[chan];
					chan_idelta[chan] = (short)((AdaptationTable[bytecode] * idelta) >> 8);  /* => / 256 => FIXED_POINT_ADAPTATION_BASE == 256 */
					if (chan_idelta[chan] < 16)
						chan_idelta[chan] = 16;
					if ((bytecode & 0x8) > 0)
						bytecode -= 0x10;

					predict = ((samples[k - channels] * AdaptCoeff1[bpred[chan]])
								+ (samples[k - 2 * channels] * AdaptCoeff2[bpred[chan]])) >> 8; /* => / 256 => FIXED_POINT_COEFF_BASE == 256 */
					current = (bytecode * idelta) + predict;

					if (current > 32767)
						current = 32767;
					else if (current < -32768)
						current = -32768;

					samples[k] = (short)current;
				}

				return ++currentBlock >= numBlocks;
			}

			private static short AssertPred(byte value)
			{
				if (value > MsADPCMAdaptCoeffCount)
					throw new Exception(string.Format("MS ADPCM synchronisation error ({0} should be < {1})", value, MsADPCMAdaptCoeffCount));

				return value;
			}
		}
	}
}
