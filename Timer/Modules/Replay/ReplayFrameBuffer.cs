/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo and Kxnrl
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using Source2Surf.Timer.Shared.Models.Replay;

namespace Source2Surf.Timer.Modules.Replay;

/// <summary>
/// Simple ring buffer to avoid front-shifting allocations when trimming pre-run frames.
/// Capacity is always a power of two so the wrap can use a bitmask instead of modulo.
/// </summary>
internal sealed class ReplayFrameBuffer : IReadOnlyList<ReplayFrameData>
{
    private ReplayFrameData[] _buffer;
    private int               _head;
    private int               _mask;

    public ReplayFrameBuffer(int capacity = 64)
    {
        var size = RoundUpToPow2(capacity > 0 ? capacity : 1);
        _buffer = new ReplayFrameData[size];
        _mask   = size - 1;
    }

    public int Count { get; private set; }

    public int Capacity => _buffer.Length;

    public ReplayFrameData this[int index]
    {
        get
        {
            if ((uint) index >= (uint) Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _buffer[(_head + index) & _mask];
        }
    }

    public void Add(in ReplayFrameData frame)
    {
        EnsureCapacity(Count + 1);

        _buffer[(_head + Count) & _mask] = frame;
        Count++;
    }

    public void RemoveOldest(int removeCount)
    {
        if (removeCount <= 0 || Count == 0)
        {
            return;
        }

        if (removeCount >= Count)
        {
            Clear();
            return;
        }

        _head =  (_head + removeCount) & _mask;
        Count -= removeCount;
    }

    public void Clear()
    {
        _head = 0;
        Count = 0;
    }

    public void EnsureCapacity(int capacity)
    {
        if (capacity <= _buffer.Length)
        {
            return;
        }

        var newSize = RoundUpToPow2(Math.Max(capacity, _buffer.Length * 2));
        var newArr  = new ReplayFrameData[newSize];

        // copy existing frames in order
        for (var i = 0; i < Count; i++)
        {
            newArr[i] = _buffer[(_head + i) & _mask];
        }

        _buffer = newArr;
        _mask   = newSize - 1;
        _head   = 0;
    }

    private static int RoundUpToPow2(int n)
        => n <= 1 ? 1 : (int) BitOperations.RoundUpToPowerOf2((uint) n);

    public Enumerator GetEnumerator()
        => new (this);

    IEnumerator<ReplayFrameData> IEnumerable<ReplayFrameData>.GetEnumerator()
        => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();

    public struct Enumerator : IEnumerator<ReplayFrameData>
    {
        private readonly ReplayFrameBuffer _buffer;
        private int                        _index;

        internal Enumerator(ReplayFrameBuffer buffer)
        {
            _buffer = buffer;
            _index  = -1;
        }

        public ReplayFrameData Current
            => _buffer._buffer[(_buffer._head + _index) & _buffer._mask];

        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            _index++;
            return _index < _buffer.Count;
        }

        public void Reset()
        {
            _index = -1;
        }

        public void Dispose()
        {
        }
    }
}
