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
	public static class WavReader
	{
		enum WaveType { Pcm = 0x1, MsAdpcm = 0x2, ImaAdpcm = 0x11 }

		public static bool LoadSound(Stream s, out Func<Stream> result, out short channels, out int sampleBits, out int sampleRate)
		{
			result = null;
			channels = -1;
			sampleBits = -1;
			sampleRate = -1;

			var type = s.ReadASCII(4);
			if (type != "RIFF")
				return false;

			s.ReadInt32(); // File-size
			var format = s.ReadASCII(4);
			if (format != "WAVE")
				return false;

			WaveType audioType = 0;
			var dataOffset = -1L;
			var dataSize = -1;
			var uncompressedSize = -1;
			short blockAlign = -1;
			short samplesPerBlock = 0;
			while (s.Position < s.Length)
			{
				if ((s.Position & 1) == 1)
					s.ReadByte(); // Alignment

				if (s.Position == s.Length)
					break; // Break if we aligned with end of stream

				var blockType = s.ReadASCII(4);
				switch (blockType)
				{
					case "fmt ":
						var fmtChunkSize = s.ReadInt32();
						var audioFormat = s.ReadInt16();
						audioType = (WaveType)audioFormat;

						if (!Enum.IsDefined(typeof(WaveType), audioType))
							throw new NotSupportedException("Compression type {0} is not supported.".F(audioFormat));

						channels = s.ReadInt16();
						sampleRate = s.ReadInt32();
						s.ReadInt32(); // Byte Rate
						blockAlign = s.ReadInt16();
						sampleBits = s.ReadInt16();

						if (audioType == WaveType.MsAdpcm)
						{
							sampleBits = 16; // unsure why this is different to the value above, but it needs to be 16 (!)

							s.ReadInt16(); // extra bytes
							samplesPerBlock = s.ReadInt16();

							s.ReadBytes(fmtChunkSize - 16 - 4); // read the remainder of padding
						}
						else
							s.ReadBytes(fmtChunkSize - 16);

						// pos 70
						break;
					case "fact":
						var chunkSize = s.ReadInt32();
						uncompressedSize = s.ReadInt32();
						s.ReadBytes(chunkSize - 4);
						break;
					case "data":
						dataSize = s.ReadInt32();
						dataOffset = s.Position;
						s.Position += dataSize;
						break;
					case "LIST":
					case "cue ":
						var listCueChunkSize = s.ReadInt32();
						s.ReadBytes(listCueChunkSize);
						break;
					default:
						s.Position = s.Length; // Skip to end of stream
						break;
				}
			}

			if (audioType == WaveType.ImaAdpcm)
				sampleBits = 16;

			var chan = channels;
			result = () =>
			{
				var audioStream = SegmentStream.CreateWithoutOwningStream(s, dataOffset, dataSize);
				if (audioType == WaveType.ImaAdpcm)
					return new WavStreamImaAdpcm(audioStream, dataSize, blockAlign, chan, uncompressedSize);
				else if (audioType == WaveType.MsAdpcm)
					return new WavStreamMsAdpcm(audioStream, dataSize, blockAlign, chan, samplesPerBlock);

				return audioStream; // Data is already PCM format.
			};

			return true;
		}

		public static float WaveLength(Stream s)
		{
			s.Position = 12;
			var fmt = s.ReadASCII(4);

			if (fmt != "fmt ")
				return 0;

			s.Position = 22;
			var channels = s.ReadInt16();
			var sampleRate = s.ReadInt32();

			s.Position = 34;
			var bitsPerSample = s.ReadInt16();
			var length = s.Length * 8;

			return length / (channels * sampleRate * bitsPerSample);
		}

		sealed class WavStreamImaAdpcm : ReadOnlyAdapterStream
		{
			readonly short channels;
			readonly int numBlocks;
			readonly int blockDataSize;
			readonly int outputSize;
			readonly int[] predictor;
			readonly int[] index;

			readonly byte[] interleaveBuffer;
			int outOffset;
			int currentBlock;

			public WavStreamImaAdpcm(Stream stream, int dataSize, short blockAlign, short channels, int uncompressedSize)
				: base(stream)
			{
				this.channels = channels;
				numBlocks = dataSize / blockAlign;
				blockDataSize = blockAlign - (channels * 4);
				outputSize = uncompressedSize * channels * 2;
				predictor = new int[channels];
				index = new int[channels];

				interleaveBuffer = new byte[channels * 16];
			}

			protected override bool BufferData(Stream baseStream, Queue<byte> data)
			{
				// Decode each block of IMA ADPCM data
				// Each block starts with a initial state per-channel
				for (var c = 0; c < channels; c++)
				{
					predictor[c] = baseStream.ReadInt16();
					index[c] = baseStream.ReadUInt8();
					baseStream.ReadUInt8(); // Unknown/Reserved

					// Output first sample from input
					data.Enqueue((byte)predictor[c]);
					data.Enqueue((byte)(predictor[c] >> 8));
					outOffset += 2;

					if (outOffset >= outputSize)
						return true;
				}

				// Decode and output remaining data in this block
				var blockOffset = 0;
				while (blockOffset < blockDataSize)
				{
					for (var c = 0; c < channels; c++)
					{
						// Decode 4 bytes (to 16 bytes of output) per channel
						var chunk = baseStream.ReadBytes(4);
						var decoded = ImaAdpcmReader.LoadImaAdpcmSound(chunk, ref index[c], ref predictor[c]);

						// Interleave output, one sample per channel
						var interleaveChannelOffset = 2 * c;
						for (var i = 0; i < decoded.Length; i += 2)
						{
							var interleaveSampleOffset = interleaveChannelOffset + i;
							interleaveBuffer[interleaveSampleOffset] = decoded[i];
							interleaveBuffer[interleaveSampleOffset + 1] = decoded[i + 1];
							interleaveChannelOffset += 2 * (channels - 1);
						}

						blockOffset += 4;
					}

					var outputRemaining = outputSize - outOffset;
					var toCopy = Math.Min(outputRemaining, interleaveBuffer.Length);
					for (var i = 0; i < toCopy; i++)
						data.Enqueue(interleaveBuffer[i]);

					outOffset += 16 * channels;

					if (outOffset >= outputSize)
						return true;
				}

				return ++currentBlock >= numBlocks;
			}
		}

		public sealed class WavStreamMsAdpcm : ReadOnlyAdapterStream
		{
			/* format docs https://wiki.multimedia.cx/index.php/Microsoft_ADPCM
			 */

			static readonly int[] AdaptationTable = new[]
			{
				230, 230, 230, 230, 307, 409, 512, 614,
				768, 614, 512, 409, 307, 230, 230, 230
			};

			static readonly int[] AdaptCoeff1 = new[] { 256, 512, 0, 192, 240, 460, 392 };

			static readonly int[] AdaptCoeff2 = new[] { 0, -256, 0, 64, 0, -208, -232 };

			readonly short channels;
			private readonly short samplesPerBlock;
			private readonly int blockDataSize;
			private readonly int numBlocks;

			int currentBlock;
#if RAW
			private FileStream f;
#endif

			public WavStreamMsAdpcm(Stream stream, int dataSize, short blockAlign, short channels, short samplesPerBlock)
				: base(stream)
			{
#if RAW
				var pos = stream.Position;
				using (var f = File.OpenWrite("d:\\dev\\stream.raw"))
				{
					stream.CopyTo(f);
				}

				f = File.OpenWrite("D:\\dev\\stream-wav.raw");
				stream.Position = pos;
#endif

				this.channels = channels;
				this.samplesPerBlock = samplesPerBlock;
				blockDataSize = blockAlign - channels * 7;
				numBlocks = dataSize / blockAlign;
			}

			protected override bool BufferData(Stream baseStream, Queue<byte> data)
			{
				var samples = new short[samplesPerBlock * channels];

				var empty = DecodeBlock(baseStream, samples);

				// buffer the samples
				foreach (var t in samples)
				{
#if RAW
					//f.WriteByte((byte)t);
					//f.WriteByte((byte)(t >> 8));
#endif

					WriteSample(t, data);
				}

				return empty;
			}

			void WriteSample(short t, Queue<byte> data)
			{
				data.Enqueue((byte)t);
				data.Enqueue((byte)(t >> 8));
			}

			/// <summary>
			/// Decodes a block of MS ADPCM data
			/// </summary>
			/// <param name="baseStream">The underlying stream to read data from</param>
			/// <param name="samples">A block worth of PCM samples</param>
			/// <returns>True when there is no more data or we can't continue</returns>
			bool DecodeBlock(Stream baseStream, short[] samples)
			{
				var bpred = new byte[channels];
				var chan_idelta = new short[channels];

				var s1 = new short[channels];
				var s2 = new short[channels];

				for (var c = 0; c < channels; c++)
					bpred[c] = baseStream.ReadUInt8();

				for (var c = 0; c < channels; c++)
					chan_idelta[c] = baseStream.ReadInt16();

				for (var c = 0; c < channels; c++)
					s1[c] = samples[channels + c] = baseStream.ReadInt16();

				for (var c = 0; c < channels; c++)
					s2[c] = samples[c] = baseStream.ReadInt16();

				var k = 2 * channels;
				for (var blockindx = 0; blockindx < blockDataSize; blockindx++)
				{
					var bytecode = baseStream.ReadUInt8();
					var chan = k % channels;

					var s = DecodeNibble((short)((bytecode >> 4) & 0x0F), bpred[chan], ref chan_idelta[chan], ref s1[chan], ref s2[chan]);

					// s1[chan] = s;
					samples[k] = s;
					k++;

					chan = k % channels;
					s = DecodeNibble((short)(bytecode & 0x0F), bpred[chan], ref chan_idelta[chan], ref s1[chan], ref s2[chan]);

					// s1[chan] = s;
					samples[k] = s;
					k++;
				}

				return ++currentBlock >= numBlocks;
			}

			// This code is an adaption of the logic from libsndfile
			private short DecodeNibble(short nibble, byte bpred, ref short chan_idelta, ref short s1, ref short s2)
			{
				// Compute next Adaptive Scale Factor (ASF)
				var idelta = chan_idelta;

				chan_idelta = (short)((AdaptationTable[nibble] * idelta) >> 8);
				if (chan_idelta < 16)
					chan_idelta = 16;

				// two's compliment for the nibble
				if ((nibble & 0x8) > 0)
					nibble -= 0x10;

				var predict = ((s1 * AdaptCoeff1[bpred])
							+ (s2 * AdaptCoeff2[bpred])) >> 8;

				s2 = s1;

				return s1 = ClampInt16((nibble * idelta) + predict);
			}

			private static short ClampInt16(int current)
			{
				if (current > 32767)
					current = 32767;
				else if (current < -32768)
					current = -32768;
				return (short)current;
			}
		}
	}
}
