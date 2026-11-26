using System.Security.Cryptography;

namespace MarktplaatsNotifier;

// Computes a fast perceptual fingerprint for JPEG thumbnails with zero external dependencies.
// For Baseline JPEGs (SOF0), decodes the 8x8 luminance DC coefficients into a 9x8 grid and
// builds a 64-bit dHash ("d:0123456789abcdef") so re-encoded/reposted photos match by Hamming distance.
// Falls back to a metadata-stripped scan payload hash ("s:...") for progressive or non-JPEG images.
public static class ImageHasher
{
    public static string ComputeHash(byte[] imageBytes)
    {
        if (imageBytes.Length < 16)
        {
            return "";
        }

        if (imageBytes[0] == 0xFF && imageBytes[1] == 0xD8)
        {
            if (TryComputeBaselineJpegDHash(imageBytes, out ulong dhash))
            {
                return "d:" + dhash.ToString("x16");
            }

            string scanHash = ComputeMetadataStrippedJpegHash(imageBytes);
            if (scanHash != "")
            {
                return "s:" + scanHash;
            }
        }

        byte[] sha = SHA256.HashData(imageBytes);
        return "s:" + Convert.ToHexString(sha, 0, 8).ToLowerInvariant();
    }

    public static bool AreSimilar(string hashA, string hashB, int maxHammingDistance = 6)
    {
        if (string.IsNullOrEmpty(hashA) || string.IsNullOrEmpty(hashB))
        {
            return false;
        }

        if (hashA == hashB)
        {
            return true;
        }

        if (hashA.StartsWith("d:") && hashB.StartsWith("d:") &&
            ulong.TryParse(hashA.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out ulong a) &&
            ulong.TryParse(hashB.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out ulong b))
        {
            return System.Numerics.BitOperations.PopCount(a ^ b) <= maxHammingDistance;
        }

        return false;
    }

    private static string ComputeMetadataStrippedJpegHash(byte[] data)
    {
        int pos = 2;
        while (pos + 4 < data.Length)
        {
            if (data[pos] != 0xFF)
            {
                pos++;
                continue;
            }

            while (pos < data.Length && data[pos] == 0xFF)
            {
                pos++;
            }
            if (pos >= data.Length)
            {
                break;
            }

            byte marker = data[pos++];
            if (marker == 0xD9)
            {
                break;
            }
            if (marker == 0xDA) // Start of Scan: hash image scan payload
            {
                if (pos + 2 >= data.Length)
                {
                    break;
                }
                int sosLen = (data[pos] << 8) | data[pos + 1];
                int scanStart = pos + sosLen;
                if (scanStart < data.Length)
                {
                    byte[] sha = SHA256.HashData(data.AsSpan(scanStart));
                    return Convert.ToHexString(sha, 0, 8).ToLowerInvariant();
                }
                break;
            }

            if (pos + 2 > data.Length)
            {
                break;
            }
            int segLen = (data[pos] << 8) | data[pos + 1];
            if (segLen < 2)
            {
                break;
            }
            pos += segLen;
        }

        return "";
    }

    // Decodes luminance DC coefficients of a standard Baseline JPEG (SOF0) into a 9x8 brightness grid -> 64-bit dHash
    private static bool TryComputeBaselineJpegDHash(byte[] data, out ulong dhash)
    {
        dhash = 0;
        int width = 0, height = 0, numComp = 0;
        int[] compId = new int[4];
        int[] hSamp = new int[4];
        int[] vSamp = new int[4];
        int[] dcTableSel = new int[4];
        int[] acTableSel = new int[4];
        HuffTable?[] dcTables = new HuffTable?[4];
        HuffTable?[] acTables = new HuffTable?[4];
        int restartInterval = 0;
        int scanStart = -1;
        int scanCompCount = 0;
        int[] scanCompIdx = new int[4];

        int pos = 2;
        while (pos + 2 < data.Length)
        {
            if (data[pos] != 0xFF)
            {
                pos++;
                continue;
            }
            while (pos < data.Length && data[pos] == 0xFF)
            {
                pos++;
            }
            if (pos >= data.Length)
            {
                break;
            }

            byte marker = data[pos++];
            if (marker == 0xD9 || marker == 0x00)
            {
                break;
            }
            if (marker == 0xC2) // Progressive JPEG -> fallback
            {
                return false;
            }
            if (marker >= 0xD0 && marker <= 0xD7)
            {
                continue;
            }

            if (pos + 2 > data.Length)
            {
                return false;
            }
            int len = (data[pos] << 8) | data[pos + 1];
            if (len < 2 || pos + len > data.Length)
            {
                return false;
            }

            int segStart = pos + 2;
            int segEnd = pos + len;

            if (marker == 0xC0) // SOF0 Baseline
            {
                if (segStart + 6 > segEnd || data[segStart] != 8)
                {
                    return false;
                }
                height = (data[segStart + 1] << 8) | data[segStart + 2];
                width = (data[segStart + 3] << 8) | data[segStart + 4];
                numComp = data[segStart + 5];
                if (width < 16 || height < 16 || numComp < 1 || numComp > 3)
                {
                    return false;
                }
                int p = segStart + 6;
                for (int i = 0; i < numComp; i++)
                {
                    if (p + 3 > segEnd) return false;
                    compId[i] = data[p];
                    hSamp[i] = data[p + 1] >> 4;
                    vSamp[i] = data[p + 1] & 0x0F;
                    if (hSamp[i] < 1 || hSamp[i] > 2 || vSamp[i] < 1 || vSamp[i] > 2) return false;
                    p += 3;
                }
            }
            else if (marker == 0xDD) // DRI
            {
                if (segStart + 2 <= segEnd)
                {
                    restartInterval = (data[segStart] << 8) | data[segStart + 1];
                }
            }
            else if (marker == 0xC4) // DHT
            {
                int p = segStart;
                while (p < segEnd)
                {
                    byte tcTh = data[p++];
                    int tc = tcTh >> 4;
                    int th = tcTh & 0x0F;
                    if (th > 3 || p + 16 > segEnd) return false;

                    int[] counts = new int[16];
                    int total = 0;
                    for (int i = 0; i < 16; i++)
                    {
                        counts[i] = data[p++];
                        total += counts[i];
                    }
                    if (p + total > segEnd) return false;
                    byte[] syms = new byte[total];
                    Array.Copy(data, p, syms, 0, total);
                    p += total;

                    HuffTable tbl = new HuffTable(counts, syms);
                    if (tc == 0) dcTables[th] = tbl;
                    else acTables[th] = tbl;
                }
            }
            else if (marker == 0xDA) // SOS
            {
                if (width == 0 || numComp == 0) return false;
                int p = segStart;
                scanCompCount = data[p++];
                if (scanCompCount < 1 || scanCompCount > numComp || p + scanCompCount * 2 + 3 > segEnd) return false;
                for (int i = 0; i < scanCompCount; i++)
                {
                    int cid = data[p++];
                    byte tdTa = data[p++];
                    int idx = Array.IndexOf(compId, cid, 0, numComp);
                    if (idx < 0) return false;
                    scanCompIdx[i] = idx;
                    dcTableSel[idx] = tdTa >> 4;
                    acTableSel[idx] = tdTa & 0x0F;
                }
                scanStart = segEnd;
                break;
            }

            pos = segEnd;
        }

        if (scanStart < 0 || dcTables[dcTableSel[0]] == null || acTables[acTableSel[0]] == null)
        {
            return false;
        }

        int maxH = 1, maxV = 1;
        for (int i = 0; i < numComp; i++)
        {
            if (hSamp[i] > maxH) maxH = hSamp[i];
            if (vSamp[i] > maxV) maxV = vSamp[i];
        }

        int mcuW = maxH * 8;
        int mcuH = maxV * 8;
        int mcusX = (width + mcuW - 1) / mcuW;
        int mcusY = (height + mcuH - 1) / mcuH;
        int blocksX = mcusX * hSamp[0];
        int blocksY = mcusY * vSamp[0];
        if (blocksX < 2 || blocksY < 2 || blocksX * blocksY > 100000)
        {
            return false;
        }

        double[] gridSum = new double[9 * 8];
        int[] gridCount = new int[9 * 8];
        int[] dcPred = new int[4];
        BitReader br = new BitReader(data, scanStart);

        int mcuCounter = 0;
        for (int my = 0; my < mcusY; my++)
        {
            for (int mx = 0; mx < mcusX; mx++)
            {
                if (restartInterval > 0 && mcuCounter > 0 && mcuCounter % restartInterval == 0)
                {
                    br.AlignToRestart();
                    Array.Clear(dcPred);
                }
                mcuCounter++;

                for (int sc = 0; sc < scanCompCount; sc++)
                {
                    int ci = scanCompIdx[sc];
                    HuffTable? dcT = dcTables[dcTableSel[ci]];
                    HuffTable? acT = acTables[acTableSel[ci]];
                    if (dcT == null || acT == null) return false;

                    int hs = hSamp[ci];
                    int vs = vSamp[ci];
                    for (int bv = 0; bv < vs; bv++)
                    {
                        for (int bh = 0; bh < hs; bh++)
                        {
                            int s = dcT.Decode(ref br);
                            if (s < 0 || s > 11) return false;
                            int diff = s == 0 ? 0 : Extend(br.ReadBits(s), s);
                            dcPred[ci] += diff;

                            // Skip 63 AC coefficients
                            int k = 1;
                            while (k < 64)
                            {
                                int rs = acT.Decode(ref br);
                                if (rs < 0) return false;
                                int r = rs >> 4;
                                int sz = rs & 0x0F;
                                if (sz == 0)
                                {
                                    if (r == 0) break; // EOB
                                    if (r == 15) { k += 16; continue; }
                                    break;
                                }
                                k += r;
                                br.SkipBits(sz);
                                k++;
                            }

                            if (ci == 0) // Luminance channel -> accumulate into 9x8 dHash grid
                            {
                                int bx = mx * hs + bh;
                                int by = my * vs + bv;
                                int gx = Math.Min(8, bx * 9 / blocksX);
                                int gy = Math.Min(7, by * 8 / blocksY);
                                int gi = gy * 9 + gx;
                                gridSum[gi] += dcPred[0];
                                gridCount[gi]++;
                            }
                        }
                    }
                }
            }
        }

        double[] grid = new double[9 * 8];
        for (int i = 0; i < grid.Length; i++)
        {
            grid[i] = gridCount[i] > 0 ? gridSum[i] / gridCount[i] : 0;
        }

        ulong hash = 0;
        int bit = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                if (grid[y * 9 + x] > grid[y * 9 + x + 1])
                {
                    hash |= 1UL << bit;
                }
                bit++;
            }
        }

        dhash = hash;
        return true;
    }

    private static int Extend(int v, int t)
    {
        int vt = 1 << (t - 1);
        return v < vt ? v + ((-1 << t) + 1) : v;
    }

    private struct BitReader
    {
        private readonly byte[] data;
        private int pos;
        private int bitBuf;
        private int bitCount;

        public BitReader(byte[] data, int startPos)
        {
            this.data = data;
            pos = startPos;
            bitBuf = 0;
            bitCount = 0;
        }

        public int ReadBit()
        {
            if (bitCount == 0)
            {
                if (pos >= data.Length) return -1;
                byte b = data[pos++];
                if (b == 0xFF)
                {
                    if (pos < data.Length && data[pos] == 0x00)
                    {
                        pos++; // byte stuffing
                    }
                }
                bitBuf = b;
                bitCount = 8;
            }

            bitCount--;
            return (bitBuf >> bitCount) & 1;
        }

        public int ReadBits(int count)
        {
            int val = 0;
            for (int i = 0; i < count; i++)
            {
                int b = ReadBit();
                if (b < 0) return 0;
                val = (val << 1) | b;
            }
            return val;
        }

        public void SkipBits(int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (ReadBit() < 0) break;
            }
        }

        public void AlignToRestart()
        {
            bitCount = 0;
            bitBuf = 0;
            while (pos + 1 < data.Length)
            {
                if (data[pos] == 0xFF && data[pos + 1] >= 0xD0 && data[pos + 1] <= 0xD7)
                {
                    pos += 2;
                    break;
                }
                pos++;
            }
        }
    }

    private sealed class HuffTable
    {
        private readonly int[] maxCode = new int[16];
        private readonly int[] minCode = new int[16];
        private readonly int[] valPtr = new int[16];
        private readonly byte[] syms;

        public HuffTable(int[] counts, byte[] syms)
        {
            this.syms = syms;
            int code = 0;
            int idx = 0;
            for (int i = 0; i < 16; i++)
            {
                if (counts[i] == 0)
                {
                    maxCode[i] = -1;
                }
                else
                {
                    valPtr[i] = idx;
                    minCode[i] = code;
                    code += counts[i];
                    maxCode[i] = code - 1;
                    idx += counts[i];
                }
                code <<= 1;
            }
        }

        public int Decode(ref BitReader br)
        {
            int code = 0;
            for (int i = 0; i < 16; i++)
            {
                int b = br.ReadBit();
                if (b < 0) return -1;
                code = (code << 1) | b;
                if (maxCode[i] >= 0 && code <= maxCode[i])
                {
                    return syms[valPtr[i] + (code - minCode[i])];
                }
            }
            return -1;
        }
    }
}
