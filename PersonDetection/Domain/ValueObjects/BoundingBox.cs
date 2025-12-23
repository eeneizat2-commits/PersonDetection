// PersonDetection.Domain/ValueObjects/BoundingBox.cs
namespace PersonDetection.Domain.ValueObjects
{
    public record BoundingBox
    {
        // ❌ REMOVED: public int Id { get; set; } - Value Objects don't have identity

        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }

        // Private constructor for EF Core
        private BoundingBox() { }

        public BoundingBox(int x, int y, int width, int height)
        {
            if (width <= 0) throw new ArgumentException("Width must be positive", nameof(width));
            if (height <= 0) throw new ArgumentException("Height must be positive", nameof(height));

            X = Math.Max(0, x);
            Y = Math.Max(0, y);
            Width = width;
            Height = height;
        }

        public float AspectRatio => Width > 0 ? (float)Height / Width : 0;
        public int Area => Width * Height;
        public Point Center => new(X + Width / 2, Y + Height / 2);
        public int Right => X + Width;
        public int Bottom => Y + Height;

        public BoundingBox ClampTo(int maxWidth, int maxHeight)
        {
            var newX = Math.Max(0, Math.Min(X, maxWidth - 1));
            var newY = Math.Max(0, Math.Min(Y, maxHeight - 1));
            var newWidth = Math.Min(Width, maxWidth - newX);
            var newHeight = Math.Min(Height, maxHeight - newY);

            return new BoundingBox(newX, newY, Math.Max(1, newWidth), Math.Max(1, newHeight));
        }

        public bool Intersects(BoundingBox other)
        {
            return X < other.Right &&
                   Right > other.X &&
                   Y < other.Bottom &&
                   Bottom > other.Y;
        }

        public float IoU(BoundingBox other)
        {
            var x1 = Math.Max(X, other.X);
            var y1 = Math.Max(Y, other.Y);
            var x2 = Math.Min(Right, other.Right);
            var y2 = Math.Min(Bottom, other.Bottom);

            var intersectArea = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
            if (intersectArea == 0) return 0;

            var unionArea = Area + other.Area - intersectArea;
            return (float)intersectArea / unionArea;
        }

        public override string ToString() => $"[{X},{Y},{Width},{Height}]";
    }

    public record Point(int X, int Y)
    {
        public double DistanceTo(Point other)
        {
            var dx = X - other.X;
            var dy = Y - other.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}