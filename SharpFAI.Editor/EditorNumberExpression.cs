using System.Globalization;

namespace SharpFAI.Editor;

internal static class EditorNumberExpression
{
    public static bool TryEvaluate(ReadOnlySpan<char> text, out double value)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return double.IsFinite(value);

        var parser = new Parser(text);
        return parser.ReadSum(0, out value) && parser.AtEnd;
    }

    private ref struct Parser(ReadOnlySpan<char> text)
    {
        private readonly ReadOnlySpan<char> _text = text;
        private int _index;

        public bool AtEnd
        {
            get
            {
                _ = Current;
                return _index == _text.Length;
            }
        }

        public char Current
        {
            get
            {
                while (_index < _text.Length && char.IsWhiteSpace(_text[_index])) _index++;
                return _index < _text.Length ? _text[_index] : '\0';
            }
        }

        public bool ReadSum(int depth, out double value)
        {
            if (!ReadProduct(depth, out value)) return false;
            while (Current is '+' or '-')
            {
                char op = _text[_index++];
                if (!ReadProduct(depth, out double right)) return false;
                value = op == '+' ? value + right : value - right;
                if (!double.IsFinite(value)) return false;
            }
            return true;
        }

        private bool ReadProduct(int depth, out double value)
        {
            if (!ReadOperand(depth, out value)) return false;
            while (Current is '*' or '/' or '×' or '÷')
            {
                char op = _text[_index++];
                if (!ReadOperand(depth, out double right)) return false;
                value = op is '*' or '×' ? value * right : value / right;
                if (!double.IsFinite(value)) return false;
            }
            return true;
        }

        private bool ReadOperand(int depth, out double value)
        {
            bool negative = false;
            while (Current is '+' or '-') negative ^= _text[_index++] == '-';

            if (Current == '(')
            {
                value = 0;
                if (depth >= 64) return false;
                _index++;
                if (!ReadSum(depth + 1, out value) || Current != ')') return false;
                _index++;
            }
            else if (!ReadNumber(out value)) return false;

            if (negative) value = -value;
            return true;
        }

        private bool ReadNumber(out double value)
        {
            int start = _index;
            ReadDigits();
            if (_index < _text.Length && _text[_index] == '.')
            {
                _index++;
                ReadDigits();
            }
            if (_index > start && _index < _text.Length && _text[_index] is 'e' or 'E')
            {
                _index++;
                if (_index < _text.Length && _text[_index] is '+' or '-') _index++;
                ReadDigits();
            }
            return double.TryParse(_text[start.._index], NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                double.IsFinite(value);
        }

        private void ReadDigits()
        {
            while (_index < _text.Length && char.IsAsciiDigit(_text[_index])) _index++;
        }
    }
}
