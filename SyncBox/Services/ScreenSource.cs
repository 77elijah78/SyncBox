using System;

namespace SyncBox.Services
{
    public readonly unsafe struct CapturedSurface
    {
        public CapturedSurface(byte* pointer, int stride, int x, int y, int width, int height)
        {
            Pointer = pointer;
            Stride = stride;
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public byte* Pointer { get; }

        public int Stride { get; }

        public int X { get; }

        public int Y { get; }

        public int Width { get; }

        public int Height { get; }

        public int Right => X + Width;

        public int Bottom => Y + Height;

        public bool Contains(int virtualX, int virtualY) =>
            virtualX >= X && virtualY >= Y && virtualX < Right && virtualY < Bottom;

        public byte* Pixel(int localX, int localY) =>
            Pointer + ((long)localY * Stride) + ((long)localX * 4);
    }

    public interface IScreenSource : IDisposable
    {
        string Name { get; }

        string? LastError { get; }

        bool TryBeginFrame(out CapturedSurface[] surfaces);

        void EndFrame();
    }
}