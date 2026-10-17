using System.Buffers.Binary;

namespace Ores.Net.Runtime;

/// <summary>
/// Trusted host-only, allocation-owned F32 region primitive. No guest
/// cpu/gpu syntax, actor carrier, device transfer, or implicit CPU fallback.
/// Areas are storage-bound capabilities; loans exclude competing writers.
/// </summary>
public static class OwnedF32RegionV1
{
    public enum Layout { AoS, SoA, AoSoA }
    public enum Mode { Read, Write }

    public readonly record struct Shape(Layout Layout, long Rows, int Fields, int Lanes = 1)
    {
        internal int ByteCount()
        {
            if (Rows < 0 || Fields <= 0 || Lanes <= 0 || Lanes > 1024 ||
                !Enum.IsDefined(Layout) ||
                (Layout != Layout.AoSoA && Lanes != 1) ||
                (Layout == Layout.AoSoA && (Lanes & (Lanes - 1)) != 0))
                throw new ArgumentException("invalid owned F32 layout");
            long padded = Layout == Layout.AoSoA
                ? checked((Rows / Lanes + (Rows % Lanes == 0 ? 0 : 1)) * Lanes)
                : Rows;
            long bytes = checked(checked(padded * Fields) * sizeof(float));
            if (bytes > 128L * 1024L * 1024L)
                throw new ArgumentOutOfRangeException(nameof(Rows), "F32 region exceeds host budget");
            return checked((int)bytes);
        }

        internal int Offset(long row, int field)
        {
            if (row < 0 || row >= Rows || field < 0 || field >= Fields)
                throw new ArgumentOutOfRangeException(nameof(row), "F32 coordinate outside layout");
            long element = Layout switch
            {
                Layout.AoS => checked(row * Fields + field),
                Layout.SoA => checked((long)field * Rows + row),
                Layout.AoSoA => checked((row / Lanes) * (long)Fields * Lanes +
                                        (long)field * Lanes + row % Lanes),
                _ => throw new ArgumentException("invalid F32 layout")
            };
            return checked((int)(element * sizeof(float)));
        }
    }

    internal sealed class State
    {
        internal readonly Shape Shape;
        internal readonly object Gate = new();
        internal byte[] Bytes;
        internal int Readers;
        internal bool Writer;
        internal bool Closed;
        internal State(Shape shape) { Shape = shape; Bytes = new byte[shape.ByteCount()]; }
    }

    public sealed class Area
    {
        internal readonly State Owner;
        public Mode Mode { get; }
        public int Field { get; }
        public long Begin { get; }
        public long End { get; }
        internal Area(State owner, Mode mode, int field, long begin, long end)
        {
            Owner = owner;
            Mode = mode;
            Field = field;
            Begin = begin;
            End = end;
        }
    }

    public sealed class Loan : IDisposable
    {
        private State? _state;
        private readonly bool _write;
        internal Loan(State state, bool write) { _state = state; _write = write; }

        public float Read(long row, int field)
        {
            State state = _state ?? throw new ObjectDisposedException(nameof(Loan));
            lock (state.Gate)
            {
                if (!ReferenceEquals(_state, state) || state.Closed)
                    throw new ObjectDisposedException(nameof(Loan));
                int offset = state.Shape.Offset(row, field);
                return BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(state.Bytes.AsSpan(offset, sizeof(float))));
            }
        }

        public void Write(long row, int field, float value)
        {
            State state = _state ?? throw new ObjectDisposedException(nameof(Loan));
            lock (state.Gate)
            {
                if (!ReferenceEquals(_state, state) || !_write || state.Closed)
                    throw new InvalidOperationException("no live F32 write loan");
                int offset = state.Shape.Offset(row, field);
                BinaryPrimitives.WriteInt32LittleEndian(
                    state.Bytes.AsSpan(offset, sizeof(float)),
                    BitConverter.SingleToInt32Bits(value));
            }
        }

        public void Dispose()
        {
            State? state = _state;
            if (state is null) return;
            lock (state.Gate)
            {
                if (!ReferenceEquals(_state, state)) return;
                _state = null;
                if (_write) state.Writer = false;
                else --state.Readers;
            }
        }
    }

    public sealed class HostBuffer
    {
        private readonly State _state;
        public Shape Shape => _state.Shape;
        public HostBuffer(Shape shape) { _state = new State(shape); }

        public Area Region(Mode mode, int field, long begin, long end)
        {
            lock (_state.Gate)
            {
                if (_state.Closed || !Enum.IsDefined(mode) ||
                    field < 0 || field >= Shape.Fields ||
                    begin < 0 || begin > end || end > Shape.Rows)
                    throw new ArgumentException("invalid/closed F32 region");
                return new Area(_state, mode, field, begin, end);
            }
        }

        public bool Owns(Area? area, Mode mode)
        {
            if (area is null) return false;
            lock (_state.Gate)
            {
                return !_state.Closed && ReferenceEquals(area.Owner, _state) &&
                    area.Mode == mode && area.Field >= 0 && area.Field < Shape.Fields &&
                    area.Begin >= 0 && area.Begin <= area.End && area.End <= Shape.Rows;
            }
        }

        public Loan BorrowRead()
        {
            lock (_state.Gate)
            {
                if (_state.Closed || _state.Writer || _state.Readers == int.MaxValue)
                    throw new InvalidOperationException("F32 buffer unavailable for read");
                ++_state.Readers;
                return new Loan(_state, false);
            }
        }

        public Loan BorrowWrite()
        {
            lock (_state.Gate)
            {
                if (_state.Closed || _state.Writer || _state.Readers != 0)
                    throw new InvalidOperationException("F32 buffer unavailable for write");
                _state.Writer = true;
                return new Loan(_state, true);
            }
        }

        public float Read(long row, int field)
        {
            using var loan = BorrowRead();
            return loan.Read(row, field);
        }

        public void Write(long row, int field, float value)
        {
            using var loan = BorrowWrite();
            loan.Write(row, field, value);
        }

        public void Close()
        {
            lock (_state.Gate)
            {
                if (_state.Readers != 0 || _state.Writer)
                    throw new InvalidOperationException("cannot close borrowed F32 allocation");
                if (_state.Closed) return;
                _state.Closed = true;
                Array.Clear(_state.Bytes);
                _state.Bytes = Array.Empty<byte>();
            }
        }
    }

    /// <summary>
    /// Validates capabilities before taking *both* physical storage loans.
    /// Cancellation and graph scheduling are separate and not implemented here.
    /// </summary>
    public static void ExecuteCpuRegion(HostBuffer input, HostBuffer output,
        Area source, Area destination, float scale, float bias)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (ReferenceEquals(input, output) || input.Shape != output.Shape ||
            !float.IsFinite(scale) || !float.IsFinite(bias) ||
            !input.Owns(source, Mode.Read) || !output.Owns(destination, Mode.Write) ||
            source.Field != destination.Field || source.Begin != destination.Begin ||
            source.End != destination.End)
            throw new ArgumentException("invalid owned F32 region footprint");
        using var reader = input.BorrowRead();
        using var writer = output.BorrowWrite();
        for (long row = source.Begin; row < source.End; ++row)
            writer.Write(row, destination.Field, reader.Read(row, source.Field) * scale + bias);
    }
}
