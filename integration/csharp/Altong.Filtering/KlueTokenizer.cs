// Purpose: Execute the exported case-sensitive BERT WordPiece tokenizer.
// Created: 2026-10-10
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Altong.Filtering;

public sealed class KlueTokenizer
{
    private readonly Dictionary<string,long> _vocab;
    private readonly Dictionary<string,long> _special;
    private readonly string _prefix;
    private readonly int _maxChars;
    private readonly long _unknown;
    public KlueTokenizer(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement; var model = root.GetProperty("model");
        var norm = root.GetProperty("normalizer");
        if (model.GetProperty("type").GetString() != "WordPiece" || norm.GetProperty("type").GetString() != "BertNormalizer" ||
            norm.GetProperty("lowercase").GetBoolean() || !norm.GetProperty("clean_text").GetBoolean() ||
            !norm.GetProperty("handle_chinese_chars").GetBoolean() || norm.GetProperty("strip_accents").ValueKind != JsonValueKind.Null ||
            root.GetProperty("pre_tokenizer").GetProperty("type").GetString() != "BertPreTokenizer")
            throw new NotSupportedException("Tokenizer config differs from the verified KLUE export.");
        _vocab = model.GetProperty("vocab").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt64(), StringComparer.Ordinal);
        _prefix = model.GetProperty("continuing_subword_prefix").GetString()!;
        _maxChars = model.GetProperty("max_input_chars_per_word").GetInt32();
        _unknown = _vocab[model.GetProperty("unk_token").GetString()!];
        _special = new(StringComparer.Ordinal);
        foreach (var token in root.GetProperty("added_tokens").EnumerateArray())
        {
            if (!token.GetProperty("special").GetBoolean() || token.GetProperty("normalized").GetBoolean() ||
                token.GetProperty("single_word").GetBoolean() || token.GetProperty("lstrip").GetBoolean() || token.GetProperty("rstrip").GetBoolean())
                throw new NotSupportedException("Unsupported added token flags.");
            _special.Add(token.GetProperty("content").GetString()!, token.GetProperty("id").GetInt64());
        }
        if (_vocab["[CLS]"] != 0 || _vocab["[SEP]"] != 2 || _vocab["[PAD]"] != 1 ||
            root.GetProperty("post_processor").GetProperty("type").GetString() != "TemplateProcessing")
            throw new NotSupportedException("Special token template differs.");
    }

    public long[] Encode(string text)
    {
        if (text.Length > 65536) throw new ArgumentException("Input is too large; refused silent truncation.");
        var ids = new List<long> { 0 }; int cursor = 0;
        while (cursor < text.Length)
        {
            int found = text.Length; string? special = null;
            foreach (string token in _special.Keys)
            {
                int at = text.IndexOf(token, cursor, StringComparison.Ordinal);
                if (at >= 0 && at < found) { found = at; special = token; }
            }
            EncodeOrdinary(text[cursor..found], ids);
            if (special is null) break;
            ids.Add(_special[special]); cursor = found + special.Length;
        }
        ids.Add(2);
        if (ids.Count > 512) throw new ArgumentException("Input exceeds 512 tokens; refused silent truncation.");
        return ids.ToArray();
    }

    private void EncodeOrdinary(string text, List<long> ids)
    {
        var word = new StringBuilder();
        void Flush() { if (word.Length > 0) { WordPiece(word.ToString(), ids); word.Clear(); CheckLength(); } }
        void CheckLength() { if (ids.Count > 511) throw new ArgumentException("Input exceeds 512 tokens."); }
        foreach (var rune in text.EnumerateRunes())
        {
            int n = rune.Value; var cat = Rune.GetUnicodeCategory(rune);
            bool white = Rune.IsWhiteSpace(rune);
            if (n == 0 || n == 0xfffd || (!white && cat is UnicodeCategory.Control or UnicodeCategory.Format)) continue;
            if (white) { Flush(); continue; }
            if (IsCjk(n) || IsPunctuation(n, cat)) { Flush(); WordPiece(rune.ToString(), ids); CheckLength(); }
            else word.Append(rune.ToString());
        }
        Flush();
    }
    private void WordPiece(string word, List<long> ids)
    {
        var runes = word.EnumerateRunes().Select(x => x.ToString()).ToArray();
        if (runes.Length > _maxChars) { ids.Add(_unknown); return; }
        var pieces = new List<long>(); int start = 0;
        while (start < runes.Length)
        {
            long? id = null; int end = runes.Length;
            for (; end > start; end--)
                if (_vocab.TryGetValue((start == 0 ? "" : _prefix) + string.Concat(runes[start..end]), out long v)) { id = v; break; }
            if (id is null) { ids.Add(_unknown); return; }
            pieces.Add(id.Value); start = end;
        }
        ids.AddRange(pieces);
    }
    private static bool IsPunctuation(int n, UnicodeCategory cat) =>
        n is >= 33 and <= 47 or >= 58 and <= 64 or >= 91 and <= 96 or >= 123 and <= 126 ||
        cat is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation or
        UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;
    private static bool IsCjk(int n) => n is >= 0x4e00 and <= 0x9fff or >= 0x3400 and <= 0x4dbf or
        >= 0x20000 and <= 0x2a6df or >= 0x2a700 and <= 0x2b73f or >= 0x2b740 and <= 0x2b81f or
        >= 0x2b820 and <= 0x2ceaf or >= 0xf900 and <= 0xfaff or >= 0x2f800 and <= 0x2fa1f;
}
