using System.Text.RegularExpressions;
using DocRouter.Core;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace DocRouter.Infrastructure;

/// <summary>
/// Картинка штампа по шаблону из StampCatalog: PNG-фон (или прозрачный холст) + строки текста с подстановками {ПОЛЕ} и {Date}.
/// Для кириллицы в контейнере нужны шрифты (в Dockerfile: fonts-dejavu-core).
/// </summary>
public sealed partial class SkiaStampRenderer(IOptions<DirectoriesOptions> dirs, TimeProvider time) : IStampRenderer
{
    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();

    public StampImage Render(string stampName, IReadOnlyDictionary<string, string> data)
    {
        if (!StampCatalog.All.TryGetValue(stampName, out var o))
            throw new InvalidOperationException($"штамп не описан в StampCatalog");

        using var bitmap = LoadOrCreate(o);
        using var canvas = new SKCanvas(bitmap);
        using var typeface = SKTypeface.FromFamilyName(o.FontFamily);

        foreach (var line in o.Lines)
        {
            using var paint = new SKPaint
            {
                Color = SKColor.Parse(o.Color),
                TextSize = line.FontSize,
                IsAntialias = true,
                Typeface = typeface
            };
            canvas.DrawText(Fill(line.Text, data, o), line.X, line.Y, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return new StampImage(encoded.ToArray(), new StampPlacement(o.Page, o.X, o.Y, o.WidthPt, o.HeightPt));
    }

    private SKBitmap LoadOrCreate(StampOptions o)
    {
        if (!string.IsNullOrEmpty(o.TemplateFile))
        {
            var path = Path.Combine(dirs.Value.StampTemplates, o.TemplateFile);
            if (!File.Exists(path)) throw new InvalidOperationException($"нет файла шаблона {path}");
            return SKBitmap.Decode(path) ?? throw new InvalidOperationException($"не удалось прочитать шаблон {path}");
        }
        var bmp = new SKBitmap(o.Width, o.Height);
        using var c = new SKCanvas(bmp);
        c.Clear(SKColors.Transparent);
        return bmp;
    }

    private string Fill(string template, IReadOnlyDictionary<string, string> data, StampOptions o) =>
        Placeholder().Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            if (key.Equals("Date", StringComparison.OrdinalIgnoreCase))
            {
                var tz = string.IsNullOrEmpty(o.TimeZone) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(o.TimeZone);
                return TimeZoneInfo.ConvertTime(time.GetUtcNow(), tz).ToString(o.DateFormat);
            }
            var hit = data.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            return hit.Value ?? "";
        });
}
