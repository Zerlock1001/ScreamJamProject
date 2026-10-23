using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class GifClip
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public Sprite[] Frames { get; private set; }
    public float[] Delays { get; private set; }
    public Sprite First => Frames != null && Frames.Length > 0 ? Frames[0] : null;
    public string SourceName { get; set; }
    public byte[] SourceBytes { get; set; }

    public static GifClip Decode(byte[] data)
    {
        if (data == null || data.Length < 13 || data[0] != 'G' || data[1] != 'I' || data[2] != 'F')
            return null;

        int width = BitConverter.ToUInt16(data, 6);
        int height = BitConverter.ToUInt16(data, 8);
        if (width <= 0 || height <= 0)
            return null;

        int packed = data[10];
        int cursor = 13;
        byte[] global = null;
        if ((packed & 0x80) != 0)
        {
            int colors = 1 << ((packed & 7) + 1);
            global = new byte[colors * 3];
            Buffer.BlockCopy(data, cursor, global, 0, global.Length);
            cursor += global.Length;
        }

        int delayCs = 10;
        int transparent = -1;
        int disposal = 0;
        var frames = new List<Sprite>();
        var delays = new List<float>();
        var canvas = new byte[width * height];
        byte[] previous = null;

        while (cursor < data.Length)
        {
            int block = data[cursor++];
            if (block == 0x3B)
                break;

            if (block == 0x21)
            {
                int label = data[cursor++];
                if (label == 0xF9)
                {
                    int size = data[cursor++];
                    int flags = data[cursor];
                    disposal = (flags >> 2) & 7;
                    delayCs = BitConverter.ToUInt16(data, cursor + 1);
                    transparent = (flags & 1) != 0 ? data[cursor + 3] : -1;
                    cursor += size;
                    if (cursor < data.Length && data[cursor] == 0)
                        cursor++;
                }
                else
                {
                    cursor = SkipSubBlocks(data, cursor);
                }

                continue;
            }

            if (block != 0x2C)
                break;

            int left = BitConverter.ToUInt16(data, cursor);
            int top = BitConverter.ToUInt16(data, cursor + 2);
            int frameWidth = BitConverter.ToUInt16(data, cursor + 4);
            int frameHeight = BitConverter.ToUInt16(data, cursor + 6);
            int imagePacked = data[cursor + 8];
            cursor += 9;
            byte[] palette = global;
            if ((imagePacked & 0x80) != 0)
            {
                int colors = 1 << ((imagePacked & 7) + 1);
                palette = new byte[colors * 3];
                Buffer.BlockCopy(data, cursor, palette, 0, palette.Length);
                cursor += palette.Length;
            }

            if (palette == null)
                break;

            bool interlace = (imagePacked & 0x40) != 0;
            int minCodeSize = data[cursor++];
            byte[] lzw = ReadSubBlocks(data, ref cursor);
            byte[] indices = DecodeLzw(lzw, minCodeSize, frameWidth * frameHeight);
            if (disposal == 3)
                previous = (byte[])canvas.Clone();

            Blit(canvas, indices, width, left, top, frameWidth, frameHeight, transparent, interlace);
            frames.Add(MakeSprite(canvas, palette, width, height));
            delays.Add(Mathf.Max(0.02f, (delayCs == 0 ? 10 : delayCs) / 100f));

            if (disposal == 2)
                Clear(canvas, width, left, top, frameWidth, frameHeight);
            else if (disposal == 3 && previous != null)
            {
                canvas = previous;
                previous = null;
            }
        }

        if (frames.Count == 0)
            return null;

        return new GifClip
        {
            Width = width,
            Height = height,
            Frames = frames.ToArray(),
            Delays = delays.ToArray()
        };
    }

    static void Blit(byte[] canvas, byte[] indices, int width, int left, int top, int frameWidth, int frameHeight, int transparent, bool interlace)
    {
        int source = 0;
        if (!interlace)
        {
            for (int y = 0; y < frameHeight; y++)
            {
                int row = (top + y) * width + left;
                for (int x = 0; x < frameWidth && source < indices.Length; x++)
                {
                    byte index = indices[source++];
                    if (index != transparent)
                        canvas[row + x] = index;
                }
            }

            return;
        }

        int[] starts = { 0, 4, 2, 1 };
        int[] steps = { 8, 8, 4, 2 };
        for (int pass = 0; pass < 4; pass++)
        {
            for (int y = starts[pass]; y < frameHeight; y += steps[pass])
            {
                int row = (top + y) * width + left;
                for (int x = 0; x < frameWidth && source < indices.Length; x++)
                {
                    byte index = indices[source++];
                    if (index != transparent)
                        canvas[row + x] = index;
                }
            }
        }
    }

    static void Clear(byte[] canvas, int width, int left, int top, int frameWidth, int frameHeight)
    {
        for (int y = 0; y < frameHeight; y++)
        {
            int row = (top + y) * width + left;
            for (int x = 0; x < frameWidth; x++)
                canvas[row + x] = 0;
        }
    }

    static Sprite MakeSprite(byte[] indices, byte[] palette, int width, int height)
    {
        var pixels = new Color32[width * height];
        for (int y = 0; y < height; y++)
        {
            int source = y * width;
            int destination = (height - 1 - y) * width;
            for (int x = 0; x < width; x++)
            {
                int index = indices[source + x] * 3;
                byte red = index + 2 < palette.Length ? palette[index] : (byte)0;
                byte green = index + 2 < palette.Length ? palette[index + 1] : (byte)0;
                byte blue = index + 2 < palette.Length ? palette[index + 2] : (byte)0;
                pixels[destination + x] = new Color32(red, green, blue, 255);
            }
        }

        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        texture.hideFlags = HideFlags.HideAndDontSave;
        Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f), 100f);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    static int SkipSubBlocks(byte[] data, int cursor)
    {
        while (cursor < data.Length)
        {
            int size = data[cursor++];
            if (size == 0)
                break;
            cursor += size;
        }

        return cursor;
    }

    static byte[] ReadSubBlocks(byte[] data, ref int cursor)
    {
        var output = new List<byte>(4096);
        while (cursor < data.Length)
        {
            int size = data[cursor++];
            if (size == 0)
                break;
            for (int i = 0; i < size && cursor < data.Length; i++)
                output.Add(data[cursor++]);
        }

        return output.ToArray();
    }

    static byte[] DecodeLzw(byte[] source, int minCodeSize, int pixelCount)
    {
        int clear = 1 << minCodeSize;
        int eoi = clear + 1;
        int codeSize = minCodeSize + 1;
        int nextCode = eoi + 1;
        var table = new List<byte[]>(4096);
        Reset(table, clear);

        int bit = 0;
        int position = 0;
        int current = 0;
        var output = new byte[Mathf.Max(0, pixelCount)];
        int written = 0;
        byte[] previous = null;

        while (written < pixelCount && position <= source.Length)
        {
            int code = 0;
            bool shortRead = false;
            for (int i = 0; i < codeSize; i++)
            {
                if (bit == 0)
                {
                    if (position >= source.Length)
                    {
                        shortRead = true;
                        break;
                    }

                    current = source[position++];
                }

                code |= ((current >> bit) & 1) << i;
                bit = (bit + 1) & 7;
            }

            if (shortRead)
                break;
            if (code == clear)
            {
                Reset(table, clear);
                codeSize = minCodeSize + 1;
                nextCode = eoi + 1;
                previous = null;
                continue;
            }

            if (code == eoi)
                break;

            byte[] entry;
            if (code < table.Count && table[code] != null)
                entry = table[code];
            else if (previous != null && code == nextCode)
                entry = Append(previous, previous[0]);
            else
                break;

            for (int i = 0; i < entry.Length && written < pixelCount; i++)
                output[written++] = entry[i];

            if (previous != null && nextCode < 4096)
            {
                table.Add(Append(previous, entry[0]));
                nextCode++;
                if (codeSize < 12 && nextCode == (1 << codeSize))
                    codeSize++;
            }

            previous = entry;
        }

        return output;
    }

    static void Reset(List<byte[]> table, int clear)
    {
        table.Clear();
        for (int i = 0; i < clear; i++)
            table.Add(new[] { (byte)i });
        table.Add(null);
        table.Add(null);
    }

    static byte[] Append(byte[] prefix, byte extra)
    {
        var entry = new byte[prefix.Length + 1];
        Buffer.BlockCopy(prefix, 0, entry, 0, prefix.Length);
        entry[prefix.Length] = extra;
        return entry;
    }
}
