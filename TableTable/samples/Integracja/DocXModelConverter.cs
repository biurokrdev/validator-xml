// Plik referencyjny do wklejenia w aplikacji (CommonDocX / DocXWorkerContext). Nie jest częścią projektów TableTable,
// bo zależy od silnika wyrażeń aplikacji (DynamicExpresso.Interpreter) i kontekstu workera.
//
// W DocXWorkerContext.PrepareConversionFunctionFor<TValue>(interpreter, expression) wystarczy:
//     => DocXModelConverter.PrepareConversionFunctionFor<TValue>(interpreter, expression);

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DynamicExpresso;
using Newtonsoft.Json.Linq;
using TableTable;

namespace CommonDocXDataBinder.DocX.Utilities
{
    /// <summary>
    /// Konwersje wartości wyrażeń (JToken z JModel) na typy oczekiwane przez workery. Dwa przypadki w jednej metodzie:
    ///  1) stare workery (TableArchitect, FieldReplacer): typy proste i listy tekstów; tablica OBIEKTÓW staje się listą wierszy
    ///     (wartości właściwości w kolejności z JSON), tablica tablic – jak dotąd, pojedynczy obiekt – jednym wierszem;
    ///  2) nowy worker tabel (TableTable.RecordTableBuilder): TValue = JToken / JArray / JObject / object – token wraca bez konwersji,
    ///     bo wiązanie po nazwach (znaczniki, tagi formantów) wykonuje biblioteka na podstawie szablonu.
    /// </summary>
    public static class DocXModelConverter
    {
        public static string PrepareConversionFunctionFor<TValue>(Interpreter interpreter, string expression)
        {
            var target = typeof(TValue);

            // --- 2) nowy worker tabel: surowy token -----------------------------------------------------------------
            if (target == typeof(JToken) || target == typeof(JArray) || target == typeof(JObject) || target == typeof(object))
                return Register<TValue>(interpreter, expression, "ToToken", token => (TValue)(object)ToToken(token, target));

            // --- 1) stare workery -----------------------------------------------------------------------------------
            if (target == typeof(IEnumerable<IEnumerable<string>>)) return Register(interpreter, expression, "ToStringList2D", ToStringList2D);
            if (target == typeof(IEnumerable<string>)) return Register(interpreter, expression, "ToStringList", ToStringList);
            if (target == typeof(string)) return Register(interpreter, expression, "ToStringValue", token => ToText(AsToken(token)));
            if (target == typeof(bool)) return Register(interpreter, expression, "ToBoolValue", ToBool);
            if (target == typeof(int)) return Register(interpreter, expression, "ToIntValue", token => ToScalar<int>(token));
            if (target == typeof(long)) return Register(interpreter, expression, "ToLongValue", token => ToScalar<long>(token));
            if (target == typeof(float)) return Register(interpreter, expression, "ToFloatValue", token => ToScalar<float>(token));
            if (target == typeof(double)) return Register(interpreter, expression, "ToDoubleValue", token => ToScalar<double>(token));
            if (target == typeof(decimal)) return Register(interpreter, expression, "ToDecimalValue", token => ToScalar<decimal>(token));
            if (target == typeof(DateTime)) return Register(interpreter, expression, "ToDateTimeValue", token => ToScalar<DateTime>(token));

            throw new NotSupportedException($"Nested conversion for type {target.Name} is not registered.");
        }

        private static string Register<T>(Interpreter interpreter, string expression, string name, Func<object, T> convert)
        {
            interpreter.SetFunction(name, convert);
            return $"{name}({expression})";
        }

        // ------------------------------------------------------------------------------------------------------------
        // Przypadek 2: token bez konwersji (null → pusty token właściwego typu, żeby worker nie dostał null).
        // ------------------------------------------------------------------------------------------------------------
        private static JToken ToToken(object token, Type target)
        {
            var jt = AsToken(token);
            if (jt.Type != JTokenType.Null) return jt;
            return target == typeof(JArray) ? new JArray() : target == typeof(JObject) ? new JObject() : JValue.CreateNull();
        }

        /// <summary>Cokolwiek zwróci wyrażenie, jako JToken (listy i obiekty .NET też).</summary>
        private static JToken AsToken(object token) => token switch
        {
            null => JValue.CreateNull(),
            JToken jt => jt,
            string s => new JValue(s),
            _ => JToken.FromObject(token),
        };

        // ------------------------------------------------------------------------------------------------------------
        // Przypadek 1: listy tekstów dla TableArchitect (wiersze × kolumny) i FieldReplacer.
        // ------------------------------------------------------------------------------------------------------------
        public static List<List<string>> ToStringList2D(object token)
        {
            if (token is List<List<string>> ready) return ready;
            var jt = AsToken(token);
            if (jt.Type == JTokenType.Null) return new List<List<string>>();
            if (jt is JObject single) jt = new JArray(single);
            if (jt is not JArray rows) return new List<List<string>> { new List<string> { ToText(jt) } };

            return rows.Select(row => row switch
            {
                JArray cells => cells.Select(ToText).ToList(),
                JObject obj => obj.Properties().Select(p => ToText(p.Value)).ToList(),
                _ => new List<string> { ToText(row) },
            }).ToList();
        }

        public static List<string> ToStringList(object token)
        {
            if (token is List<string> ready) return ready;
            var jt = AsToken(token);
            if (jt.Type == JTokenType.Null) return new List<string>();
            return jt switch
            {
                JArray items => items.Select(ToText).ToList(),
                JObject obj => obj.Properties().Select(p => ToText(p.Value)).ToList(),
                _ => new List<string> { ToText(jt) },
            };
        }

        /// <summary>Wartość prosta jako tekst; zagnieżdżona tablica/obiekt (np. nazwy_zalacznikow) sklejona przecinkami.</summary>
        public static string ToText(JToken value) => value.Type switch
        {
            JTokenType.Null or JTokenType.Undefined => string.Empty,
            JTokenType.Object => string.Join(", ", ((JObject)value).Properties().Select(p => ToText(p.Value))),
            JTokenType.Array => string.Join(", ", value.Select(ToText)),
            JTokenType.Boolean => value.Value<bool>() ? "Tak" : "Nie",
            JTokenType.Date => value.Value<DateTime>().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };

        private static bool ToBool(object token)
        {
            if (token is bool b) return b;
            var jt = AsToken(token);
            if (jt.Type == JTokenType.Boolean) return jt.Value<bool>();
            var text = ToText(jt).Trim();
            return text.Equals("tak", StringComparison.OrdinalIgnoreCase) || text.Equals("true", StringComparison.OrdinalIgnoreCase)
                   || text.Equals("yes", StringComparison.OrdinalIgnoreCase) || text == "1";
        }

        private static T ToScalar<T>(object token) where T : struct
        {
            if (token is T direct) return direct;
            var jt = AsToken(token);
            if (jt.Type == JTokenType.Null) return default;
            if (jt is JValue jv && jv.Value is T boxed) return boxed;
            return (T)Convert.ChangeType(jt.ToString(), typeof(T), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Most do biblioteki TableTable: wyrażenia z definicji (replacement-property-expression, visible-property-expression)
    /// liczy silnik aplikacji. Bieżący rekord i jego numer są wystawiane jako zmienne „item” i „index”, więc w wyrażeniu
    /// można pisać np. item.data_czas_wiadomosci.StartsWith("Wysłana").
    /// </summary>
    public sealed class DocXExpressionEvaluator : IExpressionEvaluator
    {
        private readonly Interpreter _interpreter;

        public DocXExpressionEvaluator(Interpreter interpreter) => _interpreter = interpreter;

        public object Evaluate(string expression, ExpressionScope scope)
        {
            _interpreter.SetVariable("item", scope.Current);
            _interpreter.SetVariable("index", scope.Index);
            var value = _interpreter.Eval(expression);
            return value is JToken jt && jt.Type == JTokenType.Null ? null : value;
        }
    }
}
