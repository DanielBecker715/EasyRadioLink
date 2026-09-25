using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using EasyRadioLink.Common.Settings;
using NLog;

namespace EasyRadioLink.Client.Settings.Favourites;

/// <summary>
///     Favourite servers in <c>FavouriteServers.csv</c> in the configuration directory (<c>%AppData%\EasyRadioLink</c>,
///     or the <c>-cfg=</c> directory). One server per line: <c>name,address,isDefault,password</c>; fields that contain
///     a comma, quote or line break are quoted CSV style ("a,b" / "a""b").
/// </summary>
public class CsvFavouriteServerStore : IFavouriteServerStore
{
    public const string FileName = "FavouriteServers.csv";

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly string _fileNameAndPath;

    public CsvFavouriteServerStore()
    {
        _fileNameAndPath = Path.Combine(GlobalSettingsStore.Path, FileName);
    }

    public IEnumerable<ServerAddress> LoadFromStore()
    {
        try
        {
            if (File.Exists(_fileNameAndPath)) return ReadFile();
        }
        catch (Exception exception)
        {
            var message = $"Failed to load the favourite servers: {exception.Message}";
            Logger.Error(exception, message);
            MessageBox.Show(message);
        }

        return Enumerable.Empty<ServerAddress>();
    }

    public bool SaveToStore(IEnumerable<ServerAddress> addresses)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var address in addresses)
                sb.AppendLine(string.Join(",",
                    Escape(address.Name),
                    Escape(address.Address),
                    address.IsDefault ? "True" : "False",
                    Escape(address.Password)));

            File.WriteAllText(_fileNameAndPath, sb.ToString(), new UTF8Encoding(false, true));

            return true;
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Failed to save the favourite servers");
        }

        return false;
    }

    private IEnumerable<ServerAddress> ReadFile()
    {
        var allLines = File.ReadAllLines(_fileNameAndPath);
        IList<ServerAddress> addresses = new List<ServerAddress>();

        foreach (var line in allLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var address = Parse(line);
                addresses.Add(address);
            }
            catch (Exception ex)
            {
                // never log the password column
                Logger.Error(ex, "Failed to parse a favourite server from the csv file");
            }
        }

        return addresses;
    }

    private static ServerAddress Parse(string line)
    {
        var split = SplitCsvLine(line);
        if (split.Count >= 3)
        {
            if (bool.TryParse(split[2], out var isDefault))
                return new ServerAddress(split[0], split[1],
                    split.Count >= 4 && !string.IsNullOrWhiteSpace(split[3]) ? split[3] : null, isDefault);
            throw new ArgumentException("isDefault parameter cannot be cast to a boolean");
        }

        throw new ArgumentOutOfRangeException(nameof(line), @"address must be at least 3 segments");
    }

    private static string Escape(string value)
    {
        value ??= "";

        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return value;

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"' && current.Length == 0)
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
