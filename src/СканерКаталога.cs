using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ScriptEngine.Machine;
using ScriptEngine.Machine.Contexts;
using OneScript.Contexts;

namespace ChangeIndexer
{
    /// <summary>
    /// Кроссплатформенный сканер каталогов на .NET.
    /// Выполняет быстрое рекурсивное сканирование файловой системы
    /// с использованием System.IO.DirectoryInfo.EnumerateFiles.
    ///
    /// Результат — список записей формата:
    ///   относительный_путь\tразмер\tдата(yyyyMMddHHmmss)
    /// Пути нормализованы: всегда прямой слеш (/), регистр оригинальный.
    /// </summary>
    [ContextClass("СканерКаталога", "DirectoryScanner")]
    public class DirectoryScanner : AutoContext<DirectoryScanner>
    {
        [ScriptConstructor]
        public static IRuntimeContextInstance Constructor()
        {
            return new DirectoryScanner();
        }

        /// <summary>
        /// Сканировать каталог и записать индекс в файл.
        /// </summary>
        [ContextMethod("СканироватьВФайл", "ScanToFile")]
        public int ScanToFile(string directory, string outputFile, string exclusions = "")
        {
            var entries = ScanEntries(directory, exclusions);

            var outDir = Path.GetDirectoryName(outputFile);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            File.WriteAllLines(outputFile, entries, new UTF8Encoding(false));
            return entries.Count;
        }

        /// <summary>
        /// Сканировать каталог и вернуть результат как строку.
        /// </summary>
        [ContextMethod("Сканировать", "Scan")]
        public string Scan(string directory, string exclusions = "")
        {
            var entries = ScanEntries(directory, exclusions);
            return string.Join("\n", entries);
        }

        /// <summary>
        /// Сканировать каталог, сравнить с предыдущим индексом и сохранить новый.
        /// Всё выполняется за один проход на стороне .NET без промежуточных файлов.
        ///
        /// Формат результата — многострочная строка (разделитель \n):
        ///   первая строка — количество файлов в новом индексе
        ///   далее по одной строке на каждое изменение с префиксом:
        ///     + добавленный файл
        ///     - удалённый файл
        ///     ~ изменённый файл
        /// </summary>
        [ContextMethod("ОбновитьИндекс", "UpdateIndex")]
        public string UpdateIndex(string directory, string indexFile, string exclusions = "")
        {
            return CompareWithIndex(directory, indexFile, exclusions, true);
        }

        /// <summary>
        /// Сканировать каталог и сравнить с предыдущим индексом БЕЗ сохранения.
        /// Формат результата аналогичен ОбновитьИндекс.
        /// </summary>
        [ContextMethod("ПолучитьИзменения", "GetChanges")]
        public string GetChanges(string directory, string indexFile, string exclusions = "")
        {
            return CompareWithIndex(directory, indexFile, exclusions, false);
        }

        #region Private helpers

        private string CompareWithIndex(string directory, string indexFile, string exclusions, bool save)
        {
            // 1. Сканирование
            var newEntries = ScanEntries(directory, exclusions);

            // 2. Сортировка
            newEntries.Sort(StringComparer.Ordinal);

            // 3. Загрузка старого индекса (если есть) — уже отсортирован при сохранении
            List<string> oldEntries = null;
            if (File.Exists(indexFile))
            {
                oldEntries = LoadCidxEntries(indexFile);
            }

            // 4. Сохранение нового индекса (CIDX v1)
            if (save)
                SaveCidx(indexFile, newEntries);

            // 5. Построение дельты
            var sb = new StringBuilder();
            sb.Append(newEntries.Count).Append('\n');

            if (oldEntries == null)
            {
                foreach (var e in newEntries)
                    sb.Append('+').Append(ExtractPath(e)).Append('\n');
            }
            else
            {
                BuildMergeDiff(oldEntries, newEntries, sb);
            }

            return sb.ToString();
        }

        private List<string> ScanEntries(string directory, string exclusions)
        {
            var dirInfo = new DirectoryInfo(directory);
            if (!dirInfo.Exists)
                throw new Exception("Каталог не найден: " + directory);

            var root = dirInfo.FullName;
            if (!root.EndsWith(Path.DirectorySeparatorChar))
                root += Path.DirectorySeparatorChar;

            int rootLen = root.Length;

            var excludePatterns = string.IsNullOrEmpty(exclusions)
                ? Array.Empty<string>()
                : exclusions.Split('|')
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToArray();

            var list = new List<string>(50000);

            foreach (var fi in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                string relativePath;
                if (fi.FullName.Length > rootLen)
                    relativePath = fi.FullName.Substring(rootLen).Replace('\\', '/');
                else
                    relativePath = fi.Name;

                if (IsExcluded(relativePath, excludePatterns))
                    continue;

                list.Add(string.Concat(relativePath, "\t", fi.Length.ToString(), "\t", fi.LastWriteTime.ToString("yyyyMMddHHmmss")));
            }

            return list;
        }

        private static List<string> LoadCidxEntries(string path)
        {
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length < 1 || lines[0] != "CIDX v1")
                throw new Exception("Неверный формат файла индекса: " + path);

            var result = new List<string>(Math.Max(lines.Length - 2, 0));
            for (int i = 2; i < lines.Length; i++)
            {
                if (lines[i].Length > 0)
                    result.Add(lines[i]);
            }
            return result;
        }

        private static void SaveCidx(string path, List<string> entries)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using (var sw = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                sw.WriteLine("CIDX v1");
                sw.WriteLine(entries.Count);
                foreach (var e in entries)
                    sw.WriteLine(e);
            }
        }

        private static string ExtractPath(string entry)
        {
            int pos = entry.IndexOf('\t');
            return pos > 0 ? entry.Substring(0, pos) : entry;
        }

        private static void BuildMergeDiff(List<string> oldEntries, List<string> newEntries, StringBuilder sb)
        {
            // Pre-extract paths to avoid repeated Substring calls in the merge loop
            var oldPaths = new string[oldEntries.Count];
            for (int i = 0; i < oldEntries.Count; i++)
                oldPaths[i] = ExtractPath(oldEntries[i]);

            var newPaths = new string[newEntries.Count];
            for (int i = 0; i < newEntries.Count; i++)
                newPaths[i] = ExtractPath(newEntries[i]);

            int io = 0, in_ = 0;
            while (io < oldEntries.Count && in_ < newEntries.Count)
            {
                int cmp = string.Compare(oldPaths[io], newPaths[in_], StringComparison.Ordinal);

                if (cmp == 0)
                {
                    if (oldEntries[io] != newEntries[in_])
                        sb.Append('~').Append(newPaths[in_]).Append('\n');
                    io++;
                    in_++;
                }
                else if (cmp < 0)
                {
                    sb.Append('-').Append(oldPaths[io]).Append('\n');
                    io++;
                }
                else
                {
                    sb.Append('+').Append(newPaths[in_]).Append('\n');
                    in_++;
                }
            }
            while (io < oldEntries.Count)
            {
                sb.Append('-').Append(oldPaths[io]).Append('\n');
                io++;
            }
            while (in_ < newEntries.Count)
            {
                sb.Append('+').Append(newPaths[in_]).Append('\n');
                in_++;
            }
        }

        private static bool IsExcluded(string relativePath, string[] patterns)
        {
            if (patterns.Length == 0)
                return false;

            foreach (var pattern in patterns)
            {
                if (relativePath.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        #endregion
    }
}
