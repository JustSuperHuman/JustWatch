namespace JustWatch.Core;

public class CircularBuffer<T>
{
    private readonly T[] _buffer;
    private int _start;
    private int _count;
    private readonly object _lock = new();

    public CircularBuffer(int capacity) => _buffer = new T[capacity];

    public void Add(T item)
    {
        lock (_lock)
        {
            var index = (_start + _count) % _buffer.Length;
            if (_count == _buffer.Length)
            {
                _buffer[index] = item;
                _start = (_start + 1) % _buffer.Length;
            }
            else
            {
                _buffer[index] = item;
                _count++;
            }
        }
    }

    public T[] ToArray()
    {
        lock (_lock)
        {
            var result = new T[_count];
            for (int i = 0; i < _count; i++)
                result[i] = _buffer[(_start + i) % _buffer.Length];
            return result;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _start = 0;
            _count = 0;
        }
    }
}
