using System.Globalization;
using System.IO;
using System.Text;

namespace Lumen.Transfer;

/// <summary>
/// XMODEM / XMODEM-1K / YMODEM single-file transfer engine.
/// </summary>
public static class FileTransferEngine
{
    private const byte SOH = 0x01;
    private const byte STX = 0x02;
    private const byte EOT = 0x04;
    private const byte ACK = 0x06;
    private const byte NAK = 0x15;
    private const byte CAN = 0x18;
    private const byte CRC_REQUEST = 0x43;
    private const byte SUB = 0x1A;

    private const int BlockTimeoutMs = 10000;
    private const int HandshakeTimeoutMs = 60000;
    private const int MaxRetries = 10;

    private readonly record struct Packet(byte Sequence, byte[] Data, bool CrcValid);

    public static async Task RunAsync(
        TransferOptions options,
        ITransferChannel channel,
        IProgress<TransferProgress>? progress,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(options.FilePath))
            throw new TransferException("文件路径不能为空。");

        try
        {
            if (options.Direction == TransferDirection.Send)
                await SendAsync(options, channel, progress, token).ConfigureAwait(false);
            else
                await ReceiveAsync(options, channel, progress, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TryCancelAsync(channel).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await TryCancelAsync(channel).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task SendAsync(
        TransferOptions options,
        ITransferChannel channel,
        IProgress<TransferProgress>? progress,
        CancellationToken token)
    {
        var info = new FileInfo(options.FilePath);
        if (!info.Exists) throw new TransferException($"文件不存在：{options.FilePath}");

        bool ymodem = options.Protocol == TransferProtocol.Ymodem;
        bool useCrc = true;
        if (ymodem)
        {
            await WaitForCrcHandshakeAsync(channel, HandshakeTimeoutMs, token).ConfigureAwait(false);
        }
        else
        {
            // The receiver decides between CRC and checksum via 'C' / NAK,
            // including when the user explicitly selects the checksum variant.
            useCrc = await WaitForReceiverAsync(channel, token).ConfigureAwait(false);
        }

        await using var stream = new FileStream(
            options.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            8192, FileOptions.SequentialScan);

        int blockSize = options.Protocol is TransferProtocol.Xmodem or TransferProtocol.XmodemChecksum
            ? 128
            : 1024;
        int sequence = 1;
        int blocks = 0;
        int retries = 0;
        long sent = 0;
        long total = info.Length;

        if (ymodem)
        {
            var header = BuildYmodemHeader(options.FilePath, total);
            retries += await SendPacketWithRetryAsync(
                channel, BuildPacket(false, 0, header, true), "文件头", token).ConfigureAwait(false);
            await WaitForCrcHandshakeAsync(channel, BlockTimeoutMs, token).ConfigureAwait(false);
        }

        var buffer = new byte[blockSize];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int read = await stream.ReadAsync(buffer.AsMemory(0, blockSize), token).ConfigureAwait(false);
            if (read == 0) break;

            if (read < blockSize) Array.Fill(buffer, SUB, read, blockSize - read);
            var packet = BuildPacket(blockSize == 1024, (byte)sequence, buffer, useCrc);
            retries += await SendPacketWithRetryAsync(
                channel, packet, $"第 {blocks + 1} 块", token).ConfigureAwait(false);

            sent += read;
            blocks++;
            sequence = (sequence + 1) & 0xFF;
            progress?.Report(new TransferProgress(
                sent, total, blocks, retries, $"已发送 {sent}/{total} 字节"));
        }

        retries += await SendEotAsync(channel, token).ConfigureAwait(false);

        if (ymodem)
        {
            var empty = new byte[128];
            retries += await SendPacketWithRetryAsync(
                channel, BuildPacket(false, 0, empty, true), "结束块", token).ConfigureAwait(false);
        }

        progress?.Report(new TransferProgress(total, total, blocks, retries, "发送完成"));
    }

    private static async Task ReceiveAsync(
        TransferOptions options,
        ITransferChannel channel,
        IProgress<TransferProgress>? progress,
        CancellationToken token)
    {
        bool ymodem = options.Protocol == TransferProtocol.Ymodem;
        bool checksum = options.Protocol == TransferProtocol.XmodemChecksum;
        var path = options.FilePath;
        if (Directory.Exists(path)) path = Path.Combine(path, "received.bin");

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        bool first = true;
        bool headerDone = !ymodem;
        int expectedBlock = 1;
        int blocks = 0;
        int retries = 0;
        long received = 0;
        long total = 0;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            int header = first
                ? await ReceiveHandshakeAsync(channel, checksum, token).ConfigureAwait(false)
                : await ReadByteAsync(channel, BlockTimeoutMs, token).ConfigureAwait(false);
            first = false;

            if (header < 0) throw new TransferException("等待数据包超时。");
            if (header == CAN) throw new TransferException("发送方已取消传输。");

            if (header == EOT)
            {
                await WriteByteAsync(channel, ACK, token).ConfigureAwait(false);
                if (ymodem) await FinishYmodemReceiveAsync(channel, token).ConfigureAwait(false);
                break;
            }

            if (header != SOH && header != STX)
            {
                retries++;
                await WriteByteAsync(channel, NAK, token).ConfigureAwait(false);
                continue;
            }

            int dataSize = header == STX ? 1024 : 128;
            var packet = await ReadPacketAsync(channel, dataSize, checksum, token).ConfigureAwait(false);
            if (packet is null)
            {
                retries++;
                await WriteByteAsync(channel, NAK, token).ConfigureAwait(false);
                continue;
            }

            var value = packet.Value;
            if (!value.CrcValid)
            {
                retries++;
                await WriteByteAsync(channel, NAK, token).ConfigureAwait(false);
                continue;
            }

            if (ymodem && !headerDone && value.Sequence == 0)
            {
                if (value.Data.Length == 0 || value.Data[0] == 0)
                {
                    await WriteByteAsync(channel, ACK, token).ConfigureAwait(false);
                    break;
                }

                ParseYmodemHeader(value.Data, out var fileName, out total);
                headerDone = true;
                await WriteByteAsync(channel, ACK, token).ConfigureAwait(false);
                await WriteByteAsync(channel, CRC_REQUEST, token).ConfigureAwait(false);
                progress?.Report(new TransferProgress(
                    0, total, blocks, retries, $"接收 {fileName}…"));
                continue;
            }

            if (value.Sequence == (byte)expectedBlock)
            {
                int writeCount = value.Data.Length;
                if (total > 0)
                {
                    writeCount = (int)Math.Min(writeCount, Math.Max(0, total - received));
                }

                if (writeCount > 0)
                {
                    await stream.WriteAsync(value.Data.AsMemory(0, writeCount), token).ConfigureAwait(false);
                    received += writeCount;
                }

                expectedBlock = (expectedBlock + 1) & 0xFF;
                blocks++;
                await WriteByteAsync(channel, ACK, token).ConfigureAwait(false);
                progress?.Report(new TransferProgress(
                    received, total, blocks, retries,
                    total > 0 ? $"已接收 {received}/{total} 字节" : $"已接收 {received} 字节"));
            }
            else if (value.Sequence == ((expectedBlock - 1) & 0xFF))
            {
                // The sender missed our ACK; repeat it without writing the block again.
                await WriteByteAsync(channel, ACK, token).ConfigureAwait(false);
            }
            else
            {
                retries++;
                await WriteByteAsync(channel, NAK, token).ConfigureAwait(false);
            }
        }

        await stream.FlushAsync(token).ConfigureAwait(false);
        progress?.Report(new TransferProgress(
            received, total > 0 ? total : received, blocks, retries, "接收完成"));
    }

    private static async Task<int> ReceiveHandshakeAsync(
        ITransferChannel channel, bool checksum, CancellationToken token)
    {
        long deadline = Environment.TickCount64 + HandshakeTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            token.ThrowIfCancellationRequested();
            await channel.WriteAsync(new[] { checksum ? NAK : CRC_REQUEST }, token).ConfigureAwait(false);
            int value = await ReadByteAsync(channel, 3000, token).ConfigureAwait(false);
            if (value < 0) continue;
            if (value is SOH or STX or EOT or CAN) return value;
        }
        throw new TransferException("等待发送方超时，请确认设备已开始发送文件。");
    }

    private static async Task FinishYmodemReceiveAsync(ITransferChannel channel, CancellationToken token)
    {
        await channel.WriteAsync(new[] { CRC_REQUEST }, token).ConfigureAwait(false);
        int header = await ReadByteAsync(channel, 3000, token).ConfigureAwait(false);
        if (header is not (SOH or STX)) return;

        int dataSize = header == STX ? 1024 : 128;
        var packet = await ReadPacketAsync(channel, dataSize, checksum: false, token).ConfigureAwait(false);
        if (packet is null || !packet.Value.CrcValid || packet.Value.Sequence != 0) return;
        if (packet.Value.Data.Length == 0 || packet.Value.Data[0] == 0)
        {
            await WriteByteAsync(channel, ACK, token).ConfigureAwait(false);
        }
    }

    private static async Task<bool> WaitForReceiverAsync(ITransferChannel channel, CancellationToken token)
    {
        long deadline = Environment.TickCount64 + HandshakeTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            token.ThrowIfCancellationRequested();
            int value = await ReadByteAsync(channel, 1000, token).ConfigureAwait(false);
            if (value == CRC_REQUEST) return true;
            if (value == NAK) return false;
            if (value == CAN) throw new TransferException("接收方已取消传输。");
        }
        throw new TransferException("等待接收方就绪超时，请让设备进入接收模式后重试。");
    }

    private static async Task WaitForCrcHandshakeAsync(
        ITransferChannel channel, int timeoutMs, CancellationToken token)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            token.ThrowIfCancellationRequested();
            int value = await ReadByteAsync(channel, 1000, token).ConfigureAwait(false);
            if (value == CRC_REQUEST) return;
            if (value == CAN) throw new TransferException("接收方已取消传输。");
        }
        throw new TransferException("等待接收方 CRC 握手超时。");
    }

    private static async Task<int> SendPacketWithRetryAsync(
        ITransferChannel channel, byte[] packet, string label, CancellationToken token)
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            token.ThrowIfCancellationRequested();
            await channel.WriteAsync(packet, token).ConfigureAwait(false);
            int response = await WaitForResponseAsync(channel, BlockTimeoutMs, token).ConfigureAwait(false);
            if (response == ACK) return attempt;
            if (response == CAN) throw new TransferException("接收方已取消传输。");
            if (response == -1 || response == NAK || response == CRC_REQUEST) continue;
            throw new TransferException($"{label} 收到未知响应 0x{response:X2}。");
        }
        throw new TransferException($"{label} 重试 {MaxRetries} 次后仍失败。");
    }

    private static async Task<int> SendEotAsync(ITransferChannel channel, CancellationToken token)
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            token.ThrowIfCancellationRequested();
            await channel.WriteAsync(new[] { EOT }, token).ConfigureAwait(false);
            int response = await WaitForResponseAsync(channel, BlockTimeoutMs, token).ConfigureAwait(false);
            if (response == ACK) return attempt;
            if (response == CAN) throw new TransferException("接收方已取消传输。");
            if (response == -1 || response == NAK) continue;
            throw new TransferException($"结束传输收到未知响应 0x{response:X2}。");
        }
        throw new TransferException("发送 EOT 后未收到确认。");
    }

    private static async Task<int> WaitForResponseAsync(
        ITransferChannel channel, int timeoutMs, CancellationToken token)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            int remaining = (int)Math.Max(1, deadline - Environment.TickCount64);
            int value = await ReadByteAsync(channel, remaining, token).ConfigureAwait(false);
            if (value < 0) return -1;
            if (value is ACK or NAK or CRC_REQUEST or CAN) return value;
        }
        return -1;
    }

    private static async Task<Packet?> ReadPacketAsync(
        ITransferChannel channel, int dataSize, bool checksum, CancellationToken token)
    {
        int sequence = await ReadByteAsync(channel, BlockTimeoutMs, token).ConfigureAwait(false);
        int inverse = await ReadByteAsync(channel, BlockTimeoutMs, token).ConfigureAwait(false);
        if (sequence < 0 || inverse < 0) return null;

        var data = new byte[dataSize];
        for (int i = 0; i < dataSize; i++)
        {
            int value = await ReadByteAsync(channel, BlockTimeoutMs, token).ConfigureAwait(false);
            if (value < 0) return null;
            data[i] = (byte)value;
        }

        int validationHigh = await ReadByteAsync(channel, BlockTimeoutMs, token).ConfigureAwait(false);
        if (validationHigh < 0) return null;

        bool valid;
        if (checksum)
        {
            valid = ((byte)sequence ^ (byte)inverse) == 0xFF &&
                    Checksum(data) == (byte)validationHigh;
        }
        else
        {
            int validationLow = await ReadByteAsync(channel, BlockTimeoutMs, token).ConfigureAwait(false);
            if (validationLow < 0) return null;
            ushort expected = (ushort)((validationHigh << 8) | validationLow);
            valid = ((byte)sequence ^ (byte)inverse) == 0xFF && Crc16(data) == expected;
        }
        return new Packet((byte)sequence, data, valid);
    }

    private static async Task<int> ReadByteAsync(
        ITransferChannel channel, int timeoutMs, CancellationToken token) =>
        await channel.ReadByteAsync(timeoutMs, token).ConfigureAwait(false);

    private static Task WriteByteAsync(ITransferChannel channel, byte value, CancellationToken token) =>
        channel.WriteAsync(new[] { value }, token);

    private static byte[] BuildPacket(bool oneK, byte sequence, ReadOnlySpan<byte> data, bool crc)
    {
        int size = oneK ? 1024 : 128;
        var packet = new byte[3 + size + (crc ? 2 : 1)];
        packet[0] = oneK ? STX : SOH;
        packet[1] = sequence;
        packet[2] = (byte)(255 - sequence);
        data[..Math.Min(data.Length, size)].CopyTo(packet.AsSpan(3));

        if (crc)
        {
            ushort value = Crc16(packet.AsSpan(3, size));
            packet[^2] = (byte)(value >> 8);
            packet[^1] = (byte)value;
        }
        else
        {
            packet[^1] = Checksum(packet.AsSpan(3, size));
        }
        return packet;
    }

    private static byte[] BuildYmodemHeader(string path, long size)
    {
        var header = new byte[128];
        var name = Encoding.UTF8.GetBytes(Path.GetFileName(path));
        int nameLength = Math.Min(name.Length, 100);
        Array.Copy(name, header, nameLength);
        int offset = nameLength;
        header[offset++] = 0;

        var sizeBytes = Encoding.ASCII.GetBytes(size.ToString(CultureInfo.InvariantCulture));
        int sizeLength = Math.Min(sizeBytes.Length, header.Length - offset - 2);
        Array.Copy(sizeBytes, 0, header, offset, sizeLength);
        offset += sizeLength;
        header[offset] = 0;
        return header;
    }

    private static void ParseYmodemHeader(byte[] data, out string fileName, out long size)
    {
        fileName = "received.bin";
        size = 0;
        int nameEnd = Array.IndexOf(data, (byte)0);
        if (nameEnd < 0) nameEnd = data.Length;
        if (nameEnd > 0) fileName = Encoding.UTF8.GetString(data, 0, nameEnd);

        int pos = Math.Min(data.Length, nameEnd + 1);
        int sizeEnd = Array.IndexOf(data, (byte)0, pos);
        if (sizeEnd < 0) sizeEnd = data.Length;
        if (sizeEnd > pos)
        {
            var text = Encoding.ASCII.GetString(data, pos, sizeEnd - pos).Trim();
            long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
        }
    }

    private static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte value in data)
        {
            crc ^= (ushort)(value << 8);
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ 0x1021)
                    : (ushort)(crc << 1);
            }
        }
        return crc;
    }

    private static byte Checksum(ReadOnlySpan<byte> data)
    {
        int sum = 0;
        foreach (byte value in data) sum += value;
        return (byte)sum;
    }

    private static async Task TryCancelAsync(ITransferChannel channel)
    {
        try
        {
            await channel.WriteAsync(new[] { CAN, CAN, CAN }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // the link may already be gone
        }
    }
}
