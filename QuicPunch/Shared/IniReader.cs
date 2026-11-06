#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;

public class IniReader
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> Spaces;

    public string DefaultSpace { get; set; } = "config";

    public IniReader(string data)
    {
        Spaces = DecodeCore(data);
    }

    private ConcurrentDictionary<string, ConcurrentDictionary<string, string>> DecodeCore(string data)
    {
        string currentSpace = DefaultSpace;

        ConcurrentDictionary<string, ConcurrentDictionary<string, string>> spaces = new(StringComparer.InvariantCultureIgnoreCase);
        Dictionary<string, string> values = [];

        using var sr = new StringReader(data);

        string? line;

        while ((line = sr.ReadLine()) != null)
        {
            //string line = lines[i].TrimEnd();

            if (line.Length < 3 || line[0] == ';') continue; //Ignore empty lines
            if (line[0] == '[' && line[line.Length - 1] == ']')
            {
                if (!string.IsNullOrEmpty(currentSpace) && values.Count > 0)
                {
                    spaces.TryAdd(currentSpace, new(values, StringComparer.InvariantCultureIgnoreCase));

                    values.Clear();
                }

                currentSpace = line.Substring(1, line.Length - 2);
                continue;
            }

            int index = line.IndexOf('=');

            if (index == -1)
                continue;

            string key = line.Substring(0, index).Trim();
            string value = line.Substring(index + 1).Trim();

            if (!values.ContainsKey(key))
            {
                values.Add(key, value);
            }

            values[key] = value;
        }

        spaces.TryAdd(currentSpace, new(values, StringComparer.InvariantCultureIgnoreCase));

        return spaces;
    }

    public void Merge(string iniData, bool overwrite)
    {
        var parsedSections = DecodeCore(iniData);

        foreach (var sectionPair in parsedSections)
        {
            string cat = sectionPair.Key;

            if (Spaces.TryAdd(cat, sectionPair.Value))
            {
                continue;
            }

            var existingKeyValues = Spaces[cat];

            foreach (var subKeys in sectionPair.Value)
            {
                if (existingKeyValues.ContainsKey(subKeys.Key) && !overwrite)
                {
                    continue;
                }

                existingKeyValues[subKeys.Key] = subKeys.Value;
            }
        }
    }

    public string Serialize()
    {
        StringBuilder sb = new StringBuilder();

        foreach (var space in Spaces)
        {
            if (string.IsNullOrWhiteSpace(space.Key))
                continue;

            sb.AppendLine('[' + space.Key + ']');

            foreach (var dict in space.Value)
            {
                sb.AppendLine(dict.Key + '=' + dict.Value);
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    public void SetValue(string key, string value) => SetValue(DefaultSpace, key, value);

    public void SetValue(string nameSpace, string key, string value)
    {
        if (!Spaces.ContainsKey(nameSpace))
        {
            Spaces.TryAdd(nameSpace, new());
        }

        Spaces[nameSpace][key] = value;
    }

    public string? GetValue(string key) => GetValue(DefaultSpace, key);

    public string? GetValue(string nameSpace, string key)
    {
        if (Spaces.TryGetValue(nameSpace, out var dict) && dict.TryGetValue(key, out var val))
        {
            return val;
        }

        return null;
    }
}