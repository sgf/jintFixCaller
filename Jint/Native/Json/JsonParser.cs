using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jint.Native.Object;
using Jint.Parser;
using Jint.Parser.Ast;
using Jint.Runtime;

namespace Jint.Native.Json
{
    public class JsonParser
    {
        private readonly Engine _engine;

        public JsonParser(Engine engine)
        {
            _engine = engine;
        }

        private Extra _extra;

        private int _index; // position in the stream
        private int _length; // length of the stream
        private int _lineNumber;
        private int _lineStart;
        private Location _location;
        private Token _lookahead;
        private string _source;

        private State _state;

        private static bool IsDecimalDigit(char ch)
        {
            return (ch >= '0' && ch <= '9');
        }

        private static bool IsHexDigit(char ch)
        {
            return
                ch >= '0' && ch <= '9' ||
                ch >= 'a' && ch <= 'f' ||
                ch >= 'A' && ch <= 'F'
                ;
        }

        private static bool IsOctalDigit(char ch)
        {
            return ch >= '0' && ch <= '7';
        }

        private static bool IsWhiteSpace(char ch)
        {
            return (ch == ' ')  ||
                   (ch == '\t') ||
                   (ch == '\n') ||
                   (ch == '\r');
        }

        private static bool IsLineTerminator(char ch)
        {
            return (ch == 10) || (ch == 13) || (ch == 0x2028) || (ch == 0x2029);
        }

        private char ScanHexEscape(char prefix)
        {
            int code = char.MinValue;

            int len = (prefix == 'u') ? 4 : 2;
            for (int i = 0; i < len; ++i)
            {
                if (_index < _length && IsHexDigit(_source.CharCodeAt(_index)))
                {
                    char ch = _source.CharCodeAt(_index++);
                    code = code * 16 + "0123456789abcdef".IndexOf(ch.ToString(), StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    throw new JavaScriptException(_engine.SyntaxError, string.Format("Expected hexadecimal digit:{0}", _source));
                }
            }
            return (char)code;
        }

        private void SkipWhiteSpace()
        {
            while (_index < _length)
            {
                char ch = _source.CharCodeAt(_index);

                if (IsWhiteSpace(ch))
                {
                    ++_index;
                }
                else
                {
                    break;
                }
            }
        }

        private Token ScanPunctuator()
        {
            int start = _index;
            char code = _source.CharCodeAt(_index);

            switch ((int) code)
            {
                    // Check for most common single-character punctuators.
                case 46: // . dot
                case 40: // ( open bracket
                case 41: // ) close bracket
                case 59: // ; semicolon
                case 44: // , comma
                case 123: // { open curly brace
                case 125: // } close curly brace
                case 91: // [
                case 93: // ]
                case 58: // :
                case 63: // ?
                case 126: // ~
                    ++_index;

                    return new Token
                        {
                            Type = Tokens.Punctuator,
                            Value = code.ToString(),
                            LineNumber = _lineNumber,
                            LineStart = _lineStart,
                            Range = new[] {start, _index}
                        };
            }
            throw new JavaScriptException(_engine.SyntaxError, string.Format(Messages.UnexpectedToken, code));
        }

        private Token ScanNumericLiteral()
        {
            char ch = _source.CharCodeAt(_index);

            int start = _index;
            string number = "";
            if (ch != '.')
            {
                number = _source.CharCodeAt(_index++).ToString();
                ch = _source.CharCodeAt(_index);

                // Hex number starts with '0x'.
                // Octal number starts with '0'.
                if (number == "0")
                {
                    // decimal number starts with '0' such as '09' is illegal.
                    if (ch > 0 && IsDecimalDigit(ch))
                    {
                        throw new Exception(Messages.UnexpectedToken);
                    }
                }

                while (IsDecimalDigit(_source.CharCodeAt(_index)))
                {
                    number += _source.CharCodeAt(_index++).ToString();
                }
                ch = _source.CharCodeAt(_index);
            }

            if (ch == '.')
            {
                number += _source.CharCodeAt(_index++).ToString();
                while (IsDecimalDigit(_source.CharCodeAt(_index)))
                {
                    number += _source.CharCodeAt(_index++).ToString();
                }
                ch = _source.CharCodeAt(_index);
            }

            if (ch == 'e' || ch == 'E')
            {
                number += _source.CharCodeAt(_index++).ToString();

                ch = _source.CharCodeAt(_index);
                if (ch == '+' || ch == '-')
                {
                    number += _source.CharCodeAt(_index++).ToString();
                }
                if (IsDecimalDigit(_source.CharCodeAt(_index)))
                {
                    while (IsDecimalDigit(_source.CharCodeAt(_index)))
                    {
                        number += _source.CharCodeAt(_index++).ToString();
                    }
                }
                else
                {
                    throw new Exception(Messages.UnexpectedToken);
                }
            }

            return new Token
                {
                    Type = Tokens.Number,
                    Value = Double.Parse(number, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent),
                    LineNumber = _lineNumber,
                    LineStart = _lineStart,
                    Range = new[] {start, _index}
                };
        }

        const string TrueValue  = "true";
        const string FalseValue = "false";

        private static bool IsTrueOrFalseChar(char ch)
        {
            // TODO: Should be optimized
            return TrueValue.Contains(ch.ToString()) || FalseValue.Contains(ch.ToString());
        }

        private Token ScanBooleanLiteral()
        {
            int start   = _index;
            string s    = string.Empty;
            
            while (IsTrueOrFalseChar(_source.CharCodeAt(_index)))
            {
                s += _source.CharCodeAt(_index++).ToString();
            }

            if (s == TrueValue || s == FalseValue)
            {
                return new Token
                {
                    Type = Tokens.BooleanLiteral,
                    Value = s == TrueValue,
                    LineNumber = _lineNumber,
                    LineStart = _lineStart,
                    Range = new[] { start, _index }
                };
            }
            else 
            {
                throw new JavaScriptException(_engine.SyntaxError, string.Format(Messages.UnexpectedToken, s));
            }
        }

        private Token ScanStringLiteral()
        {
            string str = "";

            char quote = _source.CharCodeAt(_index);

            int start = _index;
            ++_index;

            while (_index < _length)
            {
                char ch = _source.CharCodeAt(_index++);

                if (ch == quote)
                {
                    quote = char.MinValue;
                    break;
                }
                
                if (ch == '\\')
                {
                    ch = _source.CharCodeAt(_index++);
                    if (ch > 0 || !IsLineTerminator(ch))
                    {
                        switch (ch)
                        {
                            case 'n':
                                str += '\n';
                                break;
                            case 'r':
                                str += '\r';
                                break;
                            case 't':
                                str += '\t';
                                break;
                            case 'u':
                            case 'x':
                                int restore = _index;
                                char unescaped = ScanHexEscape(ch);
                                if (unescaped > 0)
                                {
                                    str += unescaped.ToString();
                                }
                                else
                                {
                                    _index = restore;
                                    str += ch.ToString();
                                }
                                break;
                            case 'b':
                                str += "\b";
                                break;
                            case 'f':
                                str += "\f";
                                break;
                            case 'v':
                                str += "\x0B";
                                break;

                            default:
                                if (IsOctalDigit(ch))
                                {
                                    int code = "01234567".IndexOf(ch);

                                    if (_index < _length && IsOctalDigit(_source.CharCodeAt(_index)))
                                    {
                                        code = code * 8 + "01234567".IndexOf(_source.CharCodeAt(_index++));

                                        // 3 digits are only allowed when string starts
                                        // with 0, 1, 2, 3
                                        if ("0123".IndexOf(ch) >= 0 &&
                                            _index < _length &&
                                            IsOctalDigit(_source.CharCodeAt(_index)))
                                        {
                                            code = code * 8 + "01234567".IndexOf(_source.CharCodeAt(_index++));
                                        }
                                    }
                                    str += ((char)code).ToString();
                                }
                                else
                                {
                                    str += ch.ToString();
                                }
                                break;
                        }
                    }
                    else
                    {
                        ++_lineNumber;
                        if (ch == '\r' && _source.CharCodeAt(_index) == '\n')
                        {
                            ++_index;
                        }
                    }
                }
                else if (IsLineTerminator(ch))
                {
                    break;
                }
                else
                {
                    str += ch.ToString();
                }
            }

            if (quote != 0)
            {
                throw new JavaScriptException(_engine.SyntaxError, string.Format(Messages.UnexpectedToken, _source));
            }
            
            if (StringContainsInvalidChar(str))
            {
                throw new JavaScriptException(_engine.SyntaxError, string.Format("Invalid character in string '{0}' '", str));
            }

            return new Token
                {
                    Type = Tokens.String,
                    Value = str,
                    LineNumber = _lineNumber,
                    LineStart = _lineStart,
                    Range = new[] {start, _index}
                };
        }

        private Token Advance()
        {
            SkipWhiteSpace();

            if (_index >= _length)
            {
                return new Token
                    {
                        Type = Tokens.EOF,
                        LineNumber = _lineNumber,
                        LineStart = _lineStart,
                        Range = new[] {_index, _index}
                    };
            }

            char ch = _source.CharCodeAt(_index);

            // Very common: ( and ) and ;
            if (ch == 40 || ch == 41 || ch == 58)
            {
                return ScanPunctuator();
            }

            // String literal starts with double quote (#34).
            // Single quote (#39) are not allowed in JSON.
            if (ch == 34) 
            {
                return ScanStringLiteral();
            }
            
            // Dot (.) char #46 can also start a floating-point number, hence the need
            // to check the next character.
            if (ch == 46)
            {
                if (IsDecimalDigit(_source.CharCodeAt(_index + 1)))
                {
                    return ScanNumericLiteral();
                }
                return ScanPunctuator();
            }

            if (IsDecimalDigit(ch))
            {
                return ScanNumericLiteral();
            }

            if (ch == TrueValue[0] || ch == FalseValue[0])
            {
                return ScanBooleanLiteral();
            }

            return ScanPunctuator();
        }

        private Token CollectToken()
        {
            _location = new Location
                {
                    Start = new Position
                        {
                            Line = _lineNumber,
                            Column = _index - _lineStart
                        }
                };

            Token token = Advance();
            _location.End = new Position
                {
                    Line = _lineNumber,
                    Column = _index - _lineStart
                };

            if (token.Type != Tokens.EOF)
            {
                var range = new[] {token.Range[0], token.Range[1]};
                string value = _source.Slice(token.Range[0], token.Range[1]);
                _extra.Tokens.Add(new Token
                    {
                        Type = token.Type,
                        Value = value,
                        Range = range,
                    });
            }

            return token;
        }

        private Token Lex()
        {
            Token token = _lookahead;
            _index = token.Range[1];
            _lineNumber = token.LineNumber.HasValue ? token.LineNumber.Value : 0;
            _lineStart = token.LineStart;

            _lookahead = (_extra.Tokens != null) ? CollectToken() : Advance();

            _index = token.Range[1];
            _lineNumber = token.LineNumber.HasValue ? token.LineNumber.Value : 0;
            _lineStart = token.LineStart;

            return token;
        }

        private void Peek()
        {
            int pos = _index;
            int line = _lineNumber;
            int start = _lineStart;
            _lookahead = (_extra.Tokens != null) ? CollectToken() : Advance();
            _index = pos;
            _lineNumber = line;
            _lineStart = start;
        }

        private void MarkStart()
        {
            if (_extra.Loc.HasValue)
            {
                _state.MarkerStack.Push(_index - _lineStart);
                _state.MarkerStack.Push(_lineNumber);
            }
            if (_extra.Range != null)
            {
                _state.MarkerStack.Push(_index);
            }
        }

        private T MarkEnd<T>(T node) where T : SyntaxNode
        {
            if (_extra.Range != null)
            {
                node.Range = new[] {_state.MarkerStack.Pop(), _index};
            }
            if (_extra.Loc.HasValue)
            {
                node.Location = new Location
                    {
                        Start = new Position
                            {
                                Line = _state.MarkerStack.Pop(),
                                Column = _state.MarkerStack.Pop()
                            },
                        End = new Position
                            {
                                Line = _lineNumber,
                                Column = _index - _lineStart
                            }
                    };
                PostProcess(node);
            }
            return node;
        }

        public T MarkEndIf<T>(T node) where T : SyntaxNode
        {
            if (node.Range != null || node.Location != null)
            {
                if (_extra.Loc.HasValue)
                {
                    _state.MarkerStack.Pop();
                    _state.MarkerStack.Pop();
                }
                if (_extra.Range != null)
                {
                    _state.MarkerStack.Pop();
                }
            }
            else
            {
                MarkEnd(node);
            }
            return node;
        }

        public SyntaxNode PostProcess(SyntaxNode node)
        {
            if (_extra.Source != null)
            {
                node.Location.Source = _extra.Source;
            }
            return node;
        }

        public ObjectInstance CreateArrayInstance(IEnumerable<JsValue> values)
        {
            return _engine.Array.Construct(values.ToArray());
        }

        // Throw an exception

        private void ThrowError(Token token, string messageFormat, params object[] arguments)
        {
            ParserError error;
            string msg = System.String.Format(messageFormat, arguments);

            if (token.LineNumber.HasValue)
            {
                error = new ParserError("Line " + token.LineNumber + ": " + msg)
                    {
                        Index = token.Range[0],
                        LineNumber = token.LineNumber.Value,
                        Column = token.Range[0] - _lineStart + 1
                    };
            }
            else
            {
                error = new ParserError("Line " + _lineNumber + ": " + msg)
                    {
                        Index = _index,
                        LineNumber = _lineNumber,
                        Column = _index - _lineStart + 1
                    };
            }

            error.Description = msg;
            throw error;
        }

        // Throw an exception because of the token.

        private void ThrowUnexpected(Token token)
        {
            if (token.Type == Tokens.EOF)
            {
                ThrowError(token, Messages.UnexpectedEOS);
            }

            if (token.Type == Tokens.Number)
            {
                ThrowError(token, Messages.UnexpectedNumber);
            }

            if (token.Type == Tokens.String)
            {
                ThrowError(token, Messages.UnexpectedString);
            }

            // BooleanLiteral, NullLiteral, or Punctuator.
            ThrowError(token, Messages.UnexpectedToken, token.Value as string);
        }

        // Expect the next token to match the specified punctuator.
        // If not, an exception will be thrown.

        private void Expect(string value)
        {
            Token token = Lex();
            if (token.Type != Tokens.Punctuator || !value.Equals(token.Value))
            {
                ThrowUnexpected(token);
            }
        }

        // Return true if the next token matches the specified punctuator.

        private bool Match(string value)
        {
            return _lookahead.Type == Tokens.Punctuator && value.Equals(_lookahead.Value);
        }
        
        private ObjectInstance ParseJsonArray()
        {
            var elements = new List<JsValue>();

            Expect("[");

            while (!Match("]"))
            {
                if (Match(","))
                {
                    Lex();
                    elements.Add(Null.Instance);
                }
                else
                {
                    elements.Add(ParseJsonValue());

                    if (!Match("]"))
                    {
                        Expect(",");
                    }
                }
            }

            Expect("]");

            return CreateArrayInstance(elements);
        }

        public ObjectInstance ParseJsonObject()
        {

            Expect("{");

            var obj = _engine.Object.Construct(Arguments.Empty);

            while (!Match("}"))
            {
                Tokens type = _lookahead.Type;
                if (type != Tokens.String)
                {
                    ThrowUnexpected(Lex());
                }

                var name = Lex().Value.ToString();
                if (StringContainsInvalidChar(name))
                {
                    throw new JavaScriptException(_engine.SyntaxError, string.Format("Invalid character in property name '{0}'", name));
                }

                Expect(":");
                var value = ParseJsonValue();

                if (value.IsString())
                {
                    if (StringContainsInvalidChar(value.AsString()))
                    {
                        throw new JavaScriptException(_engine.SyntaxError, string.Format("Invalid character in string '{0}' property '{1}'", value.AsString(), name));
                    }
                }

                obj.FastAddProperty(name, value, true, true, true);
                
                if (!Match("}"))
                {
                    Expect(",");
                }
            }

            Expect("}");

            return obj;
        }

        /// <summary>
        /// * @path ch15/15.12/15.12.2/15.12.2-2-3.js
        /// * @description JSON.parse - parsing an object where property name ends with a null character
        /// </summary>
        /// <param name="s"></param>
        /// <returns></returns>
        private bool StringContainsInvalidChar(string s)
        {
            for (var i = 0; i < s.Length; i++)
            {
                if ((int)s[i] <= 31) 
                    return true;
            }
            return false;
        }

        private JsValue ParseJsonValue()
        {
            Tokens type = _lookahead.Type;
            MarkStart();

            if (type == Tokens.NullLiteral)
            {
                return Null.Instance;
            }
            
            if (type == Tokens.String)
            {
                return JsValue.FromObject(Lex().Value);
            }

            if (type == Tokens.Number)
            {
                return JsValue.FromObject(Lex().Value);
            }

            if (type == Tokens.BooleanLiteral)
            {
                return (bool)Lex().Value;
            }
            
            if (Match("["))
            {
                return ParseJsonArray();
            }
            
            if (Match("{"))
            {
                return ParseJsonObject();
            }

            ThrowUnexpected(Lex());

            // can't be reached
            return Null.Instance; 
        }

        public JsValue Parse(string code)
        {
            return Parse(code, null);
        }

        public JsValue Parse(string code, ParserOptions options)
        {
            _source = code;
            _index = 0;
            _lineNumber = (_source.Length > 0) ? 1 : 0;
            _lineStart = 0;
            _length = _source.Length;
            _lookahead = null;
            _state = new State
                {
                    AllowIn = true,
                    LabelSet = new HashSet<string>(),
                    InFunctionBody = false,
                    InIteration = false,
                    InSwitch = false,
                    LastCommentStart = -1,
                    MarkerStack = new Stack<int>()
                };

            _extra = new Extra
                {
                    Range = new int[0], 
                    Loc = 0,

                };

            if (options != null)
            {
                if (!System.String.IsNullOrEmpty(options.Source))
                {
                    _extra.Source = options.Source;
                }

                if (options.Tokens)
                {
                    _extra.Tokens = new List<Token>();
                }

            }

            try
            {
                MarkStart();
                Peek();
                JsValue jsv = ParseJsonValue();

                Peek();
                Tokens type = _lookahead.Type;
                object value = _lookahead.Value;                
                if(_lookahead.Type != Tokens.EOF)
                {
                    throw new JavaScriptException(_engine.SyntaxError, string.Format("Unexpected {0} {1}", _lookahead.Type, _lookahead.Value));
                }
                return jsv;
            }
            finally
            {
                _extra = new Extra();
            }
        }

        private class Extra
        {
            public int? Loc;
            public int[] Range;
            public string Source;

            public List<Token> Tokens;
        }

        private enum Tokens
        {
            NullLiteral,
            BooleanLiteral,
            String,
            Number,
            Punctuator,
            EOF,
        };

        class Token
        {
            public Tokens Type;
            public object Value;
            public int[] Range;
            public int? LineNumber;
            public int LineStart;
        }

        static class Messages
        {
            public const string UnexpectedToken = "Unexpected token {0}";
            public const string UnexpectedNumber = "Unexpected number";
            public const string UnexpectedString = "Unexpected string";
            public const string UnexpectedEOS = "Unexpected end of input";
        };
    }
}