using System.Globalization;
using System.Text.RegularExpressions;

namespace taskforge.Services
{
    public static class OutputComparer
    {
        // нормализуем перевод строк и хвостовые пробелы
        public static string Normalize(string? s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            // 1) \r\n и \r -> \n
            s = Regex.Replace(s, @"\r\n|\r", "\n");

            // 2) убрать хвостовые пробелы/табы в КАЖДОЙ строке
            s = Regex.Replace(s, @"[ \t]+(?=\n|$)", "");

            // 3) убрать лишние пустые \n в конце
            s = s.TrimEnd('\n');

            return s;
        }

        // сравнение «умное»: построчно и по токенам, числа — с допуском
        public static bool EqualsSmart(string expectedRaw, string actualRaw, double eps = 1e-6)
        {
            var expected = Normalize(expectedRaw);
            var actual = Normalize(actualRaw);

            // Быстрый путь: полностью совпало
            if (string.Equals(expected, actual, StringComparison.Ordinal))
                return true;

            // Построчно
            var expLines = expected.Split('\n');
            var actLines = actual.Split('\n');
            if (expLines.Length != actLines.Length) return false;

            for (int i = 0; i < expLines.Length; i++)
            {
                if (!TokensEqual(expLines[i], actLines[i], eps))
                    return false;
            }
            return true;
        }

        private static bool TokensEqual(string expLine, string actLine, double eps)
        {
            // Разбиваем по любым пробелам
            var expTok = Regex.Split(expLine.Trim(), @"\s+");
            var actTok = Regex.Split(actLine.Trim(), @"\s+");
            if (expTok.Length != actTok.Length) return false;

            for (int i = 0; i < expTok.Length; i++)
            {
                var e = expTok[i];
                var a = actTok[i];

                // Если оба токена — числа, сравниваем с допуском
                if (TryParseFlexible(e, out var de) && TryParseFlexible(a, out var da))
                {
                    if (double.IsNaN(de) || double.IsNaN(da)) return false;
                    if (Math.Abs(de - da) > eps) return false;
                }
                else
                {
                    // Иначе — строгое сравнение токенов
                    if (!string.Equals(e, a, StringComparison.Ordinal))
                        return false;
                }
            }
            return true;
        }

        // Парсим число: поддерживаем и "," и "." как разделитель дробной части
        private static bool TryParseFlexible(string s, out double value)
        {
            // сначала пробуем как есть — вдруг культура уже нужная
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out value)) return true;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;

            // принудительно нормализуем разделитель: заменяем запятую на точку
            var normalized = s.Replace(',', '.');
            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
