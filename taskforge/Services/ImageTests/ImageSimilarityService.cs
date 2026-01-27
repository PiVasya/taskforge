using OpenCvSharp;

namespace taskforge.Services.ImageTests;

/// <summary>
/// Сравнение изображений для image-test.
///
/// Цель: быть устойчивым к небольшим сдвигам/масштабам.
///
/// Алгоритм (основной):
/// 1) Достаём ключевые точки ORB на эталоне и решении.
/// 2) Матчим, строим гомографию (RANSAC) и выравниваем (warp) решение в систему координат эталона.
/// 3) Считаем ошибку ТОЛЬКО по "контенту" (по маске отличий от фона), плюс штраф за лишний контент.
///
/// Фолбэк: если ORB не смог выровнять, используем сравнение по контенту без выравнивания,
/// но всё равно с маской и штрафом за пустую/лишнюю картинку.
/// </summary>
public sealed class ImageSimilarityService : IImageSimilarityService
{
    // Порог "насколько пиксель отличается от фона", чтобы считаться контентом.
    private const int ContentDiffThreshold = 18;

    // Минимум good matches, чтобы доверять гомографии.
    private const int MinGoodMatches = 12;

    public async Task<double> GetSimilarityPercentAsync(Stream expected, Stream actual, CancellationToken ct = default)
    {
        if (expected == null) throw new ArgumentNullException(nameof(expected));
        if (actual == null) throw new ArgumentNullException(nameof(actual));

        var expectedBytes = await ReadAllBytesAsync(expected, ct);
        var actualBytes = await ReadAllBytesAsync(actual, ct);

        using var refImg = DecodeToBgra(expectedBytes);
        using var actImg = DecodeToBgra(actualBytes);

        // Фон берём из углов эталона.
        var bg = EstimateBackgroundColor(refImg);

        // Маски "контента".
        using var refMask = BuildContentMask(refImg, bg);
        using var actMask = BuildContentMask(actImg, bg);

        // Если эталон вообще пустой — считаем, что любое тоже пустое.
        var refInk = Cv2.CountNonZero(refMask);
        if (refInk == 0)
        {
            var actInk = Cv2.CountNonZero(actMask);
            return actInk == 0 ? 100.0 : 0.0;
        }

        // Пытаемся ORB-align.
        using var aligned = TryAlignByOrb(refImg, actImg, refMask, actMask, bg);

        // aligned == null => фолбэк без выравнивания.
        if (aligned == null)
            return CompareByContent(refImg, actImg, refMask, actMask);

        // Для aligned строим маску контента заново (после warp).
        using var alignedMask = BuildContentMask(aligned, bg);
        return CompareByContent(refImg, aligned, refMask, alignedMask);
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream s, CancellationToken ct)
    {
        if (s.CanSeek) s.Position = 0;
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static Mat DecodeToBgra(byte[] bytes)
    {
        // Unchanged: чтобы не потерять альфу, если она есть.
        var m = Cv2.ImDecode(bytes, ImreadModes.Unchanged);
        if (m.Empty()) throw new InvalidOperationException("Failed to decode image");

        if (m.Channels() == 4)
            return m;

        if (m.Channels() == 3)
        {
            var bgra = new Mat();
            Cv2.CvtColor(m, bgra, ColorConversionCodes.BGR2BGRA);
            m.Dispose();
            return bgra;
        }

        if (m.Channels() == 1)
        {
            var bgra = new Mat();
            Cv2.CvtColor(m, bgra, ColorConversionCodes.GRAY2BGRA);
            m.Dispose();
            return bgra;
        }

        // На всякий.
        return m;
    }

    private static Scalar EstimateBackgroundColor(Mat bgra)
    {
        // Берём 4 угла и медиану по каналам.
        var w = bgra.Width;
        var h = bgra.Height;

        var pts = new[]
        {
            bgra.At<Vec4b>(0, 0),
            bgra.At<Vec4b>(0, w - 1),
            bgra.At<Vec4b>(h - 1, 0),
            bgra.At<Vec4b>(h - 1, w - 1),
        };

        static byte Med(byte a, byte b, byte c, byte d)
        {
            Span<byte> s = stackalloc byte[4] { a, b, c, d };
            s.Sort();
            return (byte)((s[1] + s[2]) / 2);
        }

        var b = Med(pts[0].Item0, pts[1].Item0, pts[2].Item0, pts[3].Item0);
        var g = Med(pts[0].Item1, pts[1].Item1, pts[2].Item1, pts[3].Item1);
        var r = Med(pts[0].Item2, pts[1].Item2, pts[2].Item2, pts[3].Item2);
        var a = Med(pts[0].Item3, pts[1].Item3, pts[2].Item3, pts[3].Item3);

        return new Scalar(b, g, r, a);
    }

        private static Mat BuildContentMask(Mat bgra, Scalar bg)
    {
        // Content = pixels that are not equal to background AND have alpha > 0 (for pngalpha renders).
        // NOTE: Mat.Split() returns Mat[] (not IDisposable), so we use ExtractChannel.

        using var alpha = new Mat();
        alpha.Create(bgra.Rows, bgra.Cols, MatType.CV_8UC1);

        if (bgra.Channels() >= 4)
        {
            // Channel index 3 = alpha for BGRA.
            Cv2.ExtractChannel(bgra, alpha, 3);
        }
        else
        {
            // No alpha channel -> treat as fully opaque.
            alpha.SetTo(Scalar.All(255));
        }

        using var bgMask = new Mat();
        Cv2.InRange(bgra, bg, bg, bgMask); // 255 where exactly background

        using var contentMask = new Mat();
        Cv2.BitwiseNot(bgMask, contentMask); // 255 where NOT background

        using var alphaMask = new Mat();
        Cv2.Threshold(alpha, alphaMask, 0, 255, ThresholdTypes.Binary); // 255 where alpha > 0

        using var combined = new Mat();
        Cv2.BitwiseAnd(contentMask, alphaMask, combined);

        // Light clean-up to reduce noise.
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.MorphologyEx(combined, combined, MorphTypes.Open, kernel);
        Cv2.MorphologyEx(combined, combined, MorphTypes.Close, kernel);

        // Caller owns the result.
        return combined.Clone();
    }

private static Mat? TryAlignByOrb(Mat refBgra, Mat actBgra, Mat refMask, Mat actMask, Scalar bg)
    {
        // ORB работает в градациях серого.
        using var refGray = new Mat();
        using var actGray = new Mat();
        Cv2.CvtColor(refBgra, refGray, ColorConversionCodes.BGRA2GRAY);
        Cv2.CvtColor(actBgra, actGray, ColorConversionCodes.BGRA2GRAY);

        // ORB (feature matching)
        using var orb = ORB.Create(
            2500,   // nFeatures
            1.2f,   // scaleFactor
            8,      // nLevels
            31,     // edgeThreshold
            0,      // firstLevel
            2,      // WTA_K
            ORBScoreType.Harris,
            31,     // patchSize
            15      // fastThreshold
        );

        KeyPoint[] kp1;
        KeyPoint[] kp2;
        using var des1 = new Mat();
        using var des2 = new Mat();

        // NOTE: In OpenCvSharp, DetectAndCompute may take descriptors as Mat (not 'out') depending on version.
        orb.DetectAndCompute(refGray, refMask, out kp1, des1);
        orb.DetectAndCompute(actGray, actMask, out kp2, des2);

        if (des1.Empty() || des2.Empty() || kp1.Length == 0 || kp2.Length == 0)
            return null;

        using var bf = new BFMatcher(NormTypes.Hamming, false);
        var knn = bf.KnnMatch(des1, des2, 2);

        var good = new List<DMatch>(512);
        foreach (var pair in knn)
        {
            if (pair.Length < 2) continue;
            var m1 = pair[0];
            var m2 = pair[1];
            if (m1.Distance < 0.75f * m2.Distance)
                good.Add(m1);
        }

        if (good.Count < MinGoodMatches)
            return null;

        var src = new Point2f[good.Count]; // actual
        var dst = new Point2f[good.Count]; // reference
        for (int i = 0; i < good.Count; i++)
        {
            var m = good[i];
            dst[i] = kp1[m.QueryIdx].Pt;
            src[i] = kp2[m.TrainIdx].Pt;
        }

        using var srcArr = InputArray.Create(src);
        using var dstArr = InputArray.Create(dst);
        using var H = Cv2.FindHomography(srcArr, dstArr, HomographyMethods.Ransac, 3.0);
        if (H.Empty())
            return null;

        // Warp actual -> reference size
        var warped = new Mat();
        Cv2.WarpPerspective(
            actBgra,
            warped,
            H,
            new Size(refBgra.Width, refBgra.Height),
            InterpolationFlags.Linear,
            BorderTypes.Constant,
            bg);

        return warped;
    }

    private static double CompareByContent(Mat refBgra, Mat actBgra, Mat refMask, Mat actMask)
    {
        // Делаем "мягкую" маску эталона, чтобы небольшие сдвиги/границы не убивали процент.
        using var refMaskDilated = new Mat();
        using var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(7, 7));
        Cv2.Dilate(refMask, refMaskDilated, k);

        // Разница по цвету внутри refMaskDilated.
        using var refBgr = new Mat();
        using var actBgr = new Mat();
        Cv2.CvtColor(refBgra, refBgr, ColorConversionCodes.BGRA2BGR);
        Cv2.CvtColor(actBgra, actBgr, ColorConversionCodes.BGRA2BGR);

        using var diff = new Mat();
        Cv2.Absdiff(refBgr, actBgr, diff);

        // Суммируем по 3 каналам.
        var diffMean = Cv2.Mean(diff, refMaskDilated);
        var meanL1 = (diffMean.Val0 + diffMean.Val1 + diffMean.Val2) / (255.0 * 3.0); // 0..1

        // Штраф за "лишний" контент: то, что есть в решении, но нет в эталоне.
        using var extra = new Mat();
        using var refInv = new Mat();
        Cv2.BitwiseNot(refMaskDilated, refInv);
        Cv2.BitwiseAnd(actMask, refInv, extra);

        var refInk = Cv2.CountNonZero(refMask);
        var extraInk = Cv2.CountNonZero(extra);

        // Штраф за "пропущенный" контент (эталон есть, решения нет).
        using var missing = new Mat();
        using var actMaskDilated = new Mat();
        Cv2.Dilate(actMask, actMaskDilated, k);
        using var actInv = new Mat();
        Cv2.BitwiseNot(actMaskDilated, actInv);
        Cv2.BitwiseAnd(refMask, actInv, missing);
        var missingInk = Cv2.CountNonZero(missing);

        // Нормируем штрафы относительно площади контента эталона.
        double extraRatio = refInk > 0 ? (double)extraInk / refInk : 1.0;
        double missingRatio = refInk > 0 ? (double)missingInk / refInk : 1.0;

        // Итоговая ошибка.
        // Цвет — основной фактор, лишнее/пропущенное — штрафы.
        var error = meanL1
                    + 0.65 * Clamp01(extraRatio)
                    + 0.45 * Clamp01(missingRatio);

        // Ограничиваем до [0..1]
        error = Clamp01(error);
        var similarity = (1.0 - error) * 100.0;

        if (similarity < 0) similarity = 0;
        if (similarity > 100) similarity = 100;
        return similarity;
    }

    private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
}
