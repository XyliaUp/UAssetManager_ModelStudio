using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace UAssetManager.ModelStudio;

internal static class OutfitStudioPreviewRenderer
{
    private const int MaximumTextureSize = 2048;
    private const int MaximumTgaPixels = 64 * 1024 * 1024;
    private static readonly Color NeutralColor = Color.FromRgb(165, 192, 186);

    public static Model3DGroup Create(OutfitStudioMeshPreview preview, IReadOnlyDictionary<int, string>? textureOverrides, CancellationToken ct)
    {
        if (preview.Vertices.Count < 3 || preview.Faces.Count == 0)
            throw new InvalidDataException(StudioStrings.Text("Service.NoFaces"));

        var normals = CreateNormals(preview, ct);
        var builders = new Dictionary<(int Material, bool HasUvs), MeshBuilder>();
        foreach (var face in preview.Faces)
        {
            ct.ThrowIfCancellationRequested();
            var hasUvs = IsFinite(face.UvA) && IsFinite(face.UvB) && IsFinite(face.UvC);
            var slot = face.Material >= 0 && face.Material < preview.Materials.Count ? face.Material : -1;
            var key = (slot, hasUvs);
            if (!builders.TryGetValue(key, out var builder))
            {
                builder = new MeshBuilder();
                builders.Add(key, builder);
            }

            builder.Append(face.A, hasUvs ? face.UvA : null, preview.Vertices, normals);
            builder.Append(face.B, hasUvs ? face.UvB : null, preview.Vertices, normals);
            builder.Append(face.C, hasUvs ? face.UvC : null, preview.Vertices, normals);
        }

        var textures = new Dictionary<string, ImageBrush?>(StringComparer.OrdinalIgnoreCase);
        var group = new Model3DGroup();
        foreach (var (key, builder) in builders)
        {
            ct.ThrowIfCancellationRequested();
            var slot = key.Material;
            var sourceMaterial = slot >= 0 ? preview.Materials[slot] : null;
            var texture = sourceMaterial?.Texture;
            if (textureOverrides is not null && textureOverrides.TryGetValue(slot, out var replacement) && !string.IsNullOrWhiteSpace(replacement))
                texture = replacement;

            var color = sourceMaterial?.Color ?? NeutralColor;
            var image = key.HasUvs ? FindTexture(texture, textures, ct) : null;
            Brush brush = image is not null ? image : new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
            var material = new DiffuseMaterial(brush);
            var model = new GeometryModel3D(builder.CreateGeometry(), material) { BackMaterial = material };
            group.Children.Add(model);
        }

        var bounds = group.Bounds;
        var radiusSquared = bounds.SizeX * bounds.SizeX + bounds.SizeY * bounds.SizeY + bounds.SizeZ * bounds.SizeZ;
        if (!double.IsFinite(radiusSquared) || radiusSquared <= 0)
            throw new InvalidDataException(StudioStrings.Text("Service.InvalidBounds"));

        ct.ThrowIfCancellationRequested();
        group.Freeze();
        return group;
    }

    public static void ValidateOverride(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || LoadTexture(path, CancellationToken.None) is null)
            throw new InvalidDataException(StudioStrings.Format("Service.OverrideTexture", "", Path.GetFileName(path)));
    }

    private static Vector3D[] CreateNormals(OutfitStudioMeshPreview preview, CancellationToken ct)
    {
        var vertices = preview.Vertices;
        foreach (var position in vertices)
        {
            ct.ThrowIfCancellationRequested();
            if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(position.Z))
                throw new InvalidDataException(StudioStrings.Text("Service.InvalidVertex"));
        }

        var normals = new Vector3D[vertices.Count];
        foreach (var face in preview.Faces)
        {
            ct.ThrowIfCancellationRequested();
            if ((uint)face.A >= (uint)vertices.Count || (uint)face.B >= (uint)vertices.Count || (uint)face.C >= (uint)vertices.Count)
                throw new InvalidDataException(StudioStrings.Text("Service.InvalidIndex"));

            var normal = Vector3D.CrossProduct(vertices[face.B] - vertices[face.A], vertices[face.C] - vertices[face.A]);
            normals[face.A] += normal;
            normals[face.B] += normal;
            normals[face.C] += normal;
        }

        for (var index = 0; index < normals.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var normal = normals[index];
            var lengthSquared = normal.LengthSquared;
            if (!double.IsFinite(lengthSquared))
                throw new InvalidDataException(StudioStrings.Text("Service.InvalidNormal"));

            if (lengthSquared > 0)
                normal /= Math.Sqrt(lengthSquared);
            else
                normal = new Vector3D(0, 1, 0);

            normals[index] = normal;
        }
        return normals;
    }

    private static bool IsFinite(Point? uv)
    {
        return uv is { } point && double.IsFinite(point.X) && double.IsFinite(point.Y);
    }

    private static ImageBrush? FindTexture(string? path, Dictionary<string, ImageBrush?> cache, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (cache.TryGetValue(path, out var brush))
            return brush;

        brush = LoadTexture(path, ct);
        cache.Add(path, brush);
        return brush;
    }

    private static ImageBrush? LoadTexture(string path, CancellationToken ct)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = string.Equals(Path.GetExtension(path), ".tga", StringComparison.OrdinalIgnoreCase)
                ? ReadTga(stream, ct)
                : ReadBitmap(stream);
            image.Freeze();

            var brush = new ImageBrush(image)
            {
                Stretch = Stretch.Fill,
                TileMode = TileMode.Tile,
                Viewbox = new Rect(0, 0, 1, 1),
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewport = new Rect(0, 0, 1, 1),
                ViewportUnits = BrushMappingMode.Absolute,
            };
            brush.Freeze();
            return brush;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException
            or InvalidDataException or FileFormatException or FormatException or ArgumentException or InvalidOperationException or COMException)
        {
            return null;
        }
    }

    private static BitmapSource ReadBitmap(Stream stream)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        stream.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        if (width > MaximumTextureSize && width >= height)
            image.DecodePixelWidth = MaximumTextureSize;
        if (height > MaximumTextureSize && height > width)
            image.DecodePixelHeight = MaximumTextureSize;
        image.EndInit();
        return image;
    }

    private static BitmapSource ReadTga(Stream stream, CancellationToken ct)
    {
        Span<byte> header = stackalloc byte[18];
        stream.ReadExactly(header);
        var type = header[2];
        var width = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(header[14..]);
        var depth = header[16];
        var descriptor = header[17];
        var gray = type is 3 or 11;
        var supportedDepth = gray ? depth == 8 : depth is 24 or 32;
        if (header[1] != 0 || type is not (2 or 3 or 10 or 11) || !supportedDepth || (descriptor & 0xc0) != 0
            || width == 0 || height == 0 || (long)width * height > MaximumTgaPixels)
        {
            throw new InvalidDataException(StudioStrings.Text("Service.TgaDimensions"));
        }

        stream.Position += header[0];
        var compressed = type is 10 or 11;
        var topOrigin = (descriptor & 0x20) != 0;
        var rightOrigin = (descriptor & 0x10) != 0;
        var hasAlpha = depth == 32 && (descriptor & 0x0f) != 0;
        var ratio = Math.Min(1, MaximumTextureSize / (double)Math.Max(width, height));
        var outputWidth = Math.Max(1, (int)Math.Round(width * ratio));
        var outputHeight = Math.Max(1, (int)Math.Round(height * ratio));
        var stride = outputWidth * 4;
        var output = new byte[stride * outputHeight];
        var sampleX = CreateSampleMap(width, outputWidth);
        var sampleY = CreateSampleMap(height, outputHeight);

        var pixelCount = (int)width * height;
        var cursor = 0;
        Span<byte> pixel = stackalloc byte[4];
        pixel = pixel[..(depth / 8)];
        while (cursor < pixelCount)
        {
            ct.ThrowIfCancellationRequested();
            var length = 1;
            var repeated = false;
            if (compressed)
            {
                var packet = stream.ReadByte();
                if (packet < 0)
                    throw new EndOfStreamException();

                length = (packet & 0x7f) + 1;
                repeated = (packet & 0x80) != 0;
                if (length > pixelCount - cursor)
                    throw new InvalidDataException(StudioStrings.Text("Service.TgaPixels"));
                if (repeated)
                    stream.ReadExactly(pixel);
            }

            for (var index = 0; index < length; index++)
            {
                if (!repeated)
                    stream.ReadExactly(pixel);

                var x = cursor % width;
                var y = cursor / width;
                cursor++;
                var outputX = sampleX[rightOrigin ? width - 1 - x : x];
                var outputY = sampleY[topOrigin ? y : height - 1 - y];
                if (outputX < 0 || outputY < 0)
                    continue;

                var start = outputY * stride + outputX * 4;
                output[start] = pixel[0];
                output[start + 1] = gray ? pixel[0] : pixel[1];
                output[start + 2] = gray ? pixel[0] : pixel[2];
                output[start + 3] = hasAlpha ? pixel[3] : (byte)255;
            }
        }

        return BitmapSource.Create(outputWidth, outputHeight, 96, 96, PixelFormats.Bgra32, null, output, stride);
    }

    private static int[] CreateSampleMap(int inputSize, int outputSize)
    {
        var map = new int[inputSize];
        Array.Fill(map, -1);
        for (var index = 0; index < outputSize; index++)
        {
            var sample = (2 * index + 1) * inputSize / (2 * outputSize);
            map[sample] = index;
        }
        return map;
    }

    private sealed class MeshBuilder
    {
        private readonly Dictionary<(int Vertex, Point? Uv), int> _corners = new();
        private readonly Point3DCollection _positions = new();
        private readonly Vector3DCollection _normals = new();
        private readonly Int32Collection _triangles = new();
        private readonly PointCollection _uvs = new();

        public void Append(int vertex, Point? uv, IReadOnlyList<Point3D> positions, IReadOnlyList<Vector3D> normals)
        {
            var key = (vertex, uv);
            if (!_corners.TryGetValue(key, out var index))
            {
                index = _positions.Count;
                _corners.Add(key, index);
                _positions.Add(positions[vertex]);
                _normals.Add(normals[vertex]);
                if (uv is { } point)
                    _uvs.Add(point);
            }
            _triangles.Add(index);
        }

        public MeshGeometry3D CreateGeometry()
        {
            return new MeshGeometry3D
            {
                Positions = _positions,
                TriangleIndices = _triangles,
                Normals = _normals,
                TextureCoordinates = _uvs,
            };
        }
    }
}
