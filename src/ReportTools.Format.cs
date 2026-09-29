using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RD = CrystalDecisions.ReportAppServer.ReportDefModel;

namespace RptMcp
{
    /// <summary>Field number/date/time formatting and conditional formulas (RAS clone + Modify).</summary>
    internal static partial class ReportTools
    {
        private static JToken SetFieldFormat(JObject a) => Edit(a, main =>
        {
            var ras = Ras.For(main, Sub(a));
            var obj = ras.Object((string)a["object"]);
            if (obj is RD.TextObject text) return SetEmbeddedFieldFormat(ras, text, a);
            if (!(obj is RD.FieldObject field))
                throw new ToolError($"'{obj.Name}' is a {obj.Kind}; only field objects and text objects with embedded fields have a number/date format.");

            var clone = (RD.FieldObject)field.Clone(true);
            var ff = clone.FieldFormat ?? (clone.FieldFormat = new RD.FieldFormat());
            var done = ApplyFormat(ff, a);

            // Custom pattern: rendered through the Display String conditional formula.
            if (a["display_format"] != null)
            {
                var pattern = (string)a["display_format"];
                var formula = string.IsNullOrEmpty(pattern) ? "" : $"ToText(CurrentFieldValue, \"{pattern.Replace("\"", "\"\"")}\")";
                Conditions.Set(clone, typeof(RD.FieldObject), "Format.DisplayString", formula);
                done.Add("display_format");
            }
            if (done.Count == 0) throw new ToolError("Nothing to change: pass at least one format option.");

            ras.ReportDef.ReportObjectController.Modify(field, clone);
            var saved = (RD.FieldObject)ras.Object(field.Name);
            return new JObject { ["object"] = field.Name, ["changed"] = done, ["format"] = FormatText(saved.FieldFormat, saved.FieldValueType.ToString()) };
        });

        /// <summary>Formats a field embedded in a text object, e.g. {T.Qty} in "{T.Qty} {T.Unit}".</summary>
        private static JObject SetEmbeddedFieldFormat(Ras ras, RD.TextObject text, JObject a)
        {
            if (a["display_format"] != null)
                throw new ToolError("display_format only works on field objects. For a field inside a text object use the individual options " +
                                    "(decimals, thousands_separator, date_order...), or put the value in a formula with ToText().");

            var clone = (RD.TextObject)text.Clone(true);
            var elements = EmbeddedFields(clone);
            if (elements.Count == 0) throw new ToolError($"Text object '{text.Name}' contains no embedded fields.");
            var wanted = (string)a["field"];
            RD.ParagraphFieldElement element;
            if (string.IsNullOrWhiteSpace(wanted))
            {
                if (elements.Count > 1)
                    throw new ToolError($"'{text.Name}' embeds several fields; pass field = one of: {string.Join(", ", elements.Select(e => e.DataSource))}.");
                element = elements[0];
            }
            else
            {
                var form = wanted.Trim().StartsWith("{") ? wanted.Trim() : "{" + wanted.Trim() + "}";
                element = elements.FirstOrDefault(e => Eq(e.DataSource, form))
                          ?? throw new ToolError($"Field {form} is not embedded in '{text.Name}'. Embedded: {string.Join(", ", elements.Select(e => e.DataSource))}.");
            }

            var ff = element.FieldFormat ?? (element.FieldFormat = new RD.FieldFormat());
            var done = ApplyFormat(ff, a);
            if (done.Count == 0) throw new ToolError("Nothing to change: pass at least one format option.");
            ras.ReportDef.ReportObjectController.Modify(text, clone);

            var saved = EmbeddedFields((RD.TextObject)ras.Object(text.Name)).First(e => Eq(e.DataSource, element.DataSource));
            return new JObject
            {
                ["object"] = text.Name, ["field"] = element.DataSource, ["changed"] = done,
                ["format"] = FormatText(saved.FieldFormat, FieldTypeName(ras, element.DataSource))
            };
        }

        internal static List<RD.ParagraphFieldElement> EmbeddedFields(RD.TextObject t)
        {
            var list = new List<RD.ParagraphFieldElement>();
            if (t?.Paragraphs == null) return list;
            foreach (RD.Paragraph p in t.Paragraphs)
                foreach (RD.ISCRParagraphElement e in p.ParagraphElements)
                    if (e is RD.ParagraphFieldElement f) list.Add(f);
            return list;
        }

        internal static string FieldTypeName(Ras ras, string formulaForm)
        {
            try { return ras.Field(formulaForm).Type.ToString(); } catch { return ""; }
        }

        /// <summary>Applies the number/date/time/boolean options in <paramref name="a"/>; returns the names of what changed.</summary>
        private static JArray ApplyFormat(RD.FieldFormat ff, JObject a)
        {
            var done = new JArray();

            // Numbers
            if (Has(a, "decimals", "rounding", "thousands_separator", "thousands_symbol", "decimal_symbol", "negative",
                    "leading_zero", "currency_symbol", "currency", "suppress_if_zero", "zero_value_string"))
            {
                var nf = ff.NumericFormat ?? (ff.NumericFormat = new RD.NumericFieldFormat());
                if (a["decimals"] != null)
                {
                    var d = (int)a["decimals"];
                    if (d < 0 || d > 10) throw new ToolError("decimals must be 0..10.");
                    nf.NDecimalPlaces = d;
                    if (a["rounding"] == null) nf.RoundingFormat = RoundingFor(d); // same as the designer does
                    done.Add("decimals");
                }
                if (a["rounding"] != null) { nf.RoundingFormat = RoundingFor((int)a["rounding"]); done.Add("rounding"); }
                if (a["thousands_separator"] != null) { nf.ThousandsSeparator = (bool)a["thousands_separator"]; done.Add("thousands_separator"); }
                if (a["thousands_symbol"] != null) { nf.ThousandSymbol = (string)a["thousands_symbol"]; nf.ThousandsSeparator = true; done.Add("thousands_symbol"); }
                if (a["decimal_symbol"] != null) { nf.DecimalSymbol = (string)a["decimal_symbol"]; done.Add("decimal_symbol"); }
                if (a["negative"] != null) { nf.NegativeFormat = Pick((string)a["negative"], NegativeMap); done.Add("negative"); }
                if (a["leading_zero"] != null) { nf.EnableUseLeadZero = (bool)a["leading_zero"]; done.Add("leading_zero"); }
                if (a["currency_symbol"] != null)
                {
                    nf.CurrencySymbol = (string)a["currency_symbol"];
                    if (a["currency"] == null) nf.CurrencySymbolFormat = string.IsNullOrEmpty((string)a["currency_symbol"])
                        ? RD.CrCurrencySymbolTypeEnum.crCurrencySymbolTypeNoSymbol : RD.CrCurrencySymbolTypeEnum.crCurrencySymbolTypeFixedSymbol;
                    done.Add("currency_symbol");
                }
                if (a["currency"] != null) { nf.CurrencySymbolFormat = Pick((string)a["currency"], CurrencyMap); done.Add("currency"); }
                if (a["suppress_if_zero"] != null) { nf.EnableSuppressIfZero = (bool)a["suppress_if_zero"]; done.Add("suppress_if_zero"); }
                if (a["zero_value_string"] != null) { nf.ZeroValueString = (string)a["zero_value_string"]; done.Add("zero_value_string"); }
            }

            // Dates
            if (Has(a, "date_order", "date_separator", "year", "month", "day"))
            {
                var df = ff.DateFormat ?? (ff.DateFormat = new RD.DateFieldFormat());
                df.SystemDefaultType = RD.CrDateSystemDefaultTypeEnum.crDateSystemDefaultTypeNotUsingDefaults;
                if (a["date_order"] != null) { df.DateOrder = Pick((string)a["date_order"], DateOrderMap); done.Add("date_order"); }
                if (a["date_separator"] != null) { df.DateFirstSeparator = df.DateSecondSeparator = (string)a["date_separator"]; done.Add("date_separator"); }
                if (a["year"] != null) { df.YearFormat = Pick((string)a["year"], YearMap); done.Add("year"); }
                if (a["month"] != null) { df.MonthFormat = Pick((string)a["month"], MonthMap); done.Add("month"); }
                if (a["day"] != null) { df.DayFormat = Pick((string)a["day"], DayMap); done.Add("day"); }
            }

            // Times
            if (Has(a, "time_base", "show_seconds", "time_separator", "am_string", "pm_string"))
            {
                var tf = ff.TimeFormat ?? (ff.TimeFormat = new RD.TimeFieldFormat());
                if (a["time_base"] != null) { tf.TimeBase = Pick((string)a["time_base"], TimeBaseMap); done.Add("time_base"); }
                if (a["show_seconds"] != null)
                {
                    tf.SecondFormat = (bool)a["show_seconds"] ? RD.CrSecondFormatEnum.crSecondFormatNumericSecond : RD.CrSecondFormatEnum.crSecondFormatNoSecond;
                    if (!(bool)a["show_seconds"]) tf.MinuteSecondSeparator = "";
                    done.Add("show_seconds");
                }
                if (a["time_separator"] != null)
                {
                    tf.HourMinuteSeparator = (string)a["time_separator"];
                    if (tf.SecondFormat != RD.CrSecondFormatEnum.crSecondFormatNoSecond) tf.MinuteSecondSeparator = (string)a["time_separator"];
                    done.Add("time_separator");
                }
                if (a["am_string"] != null) { tf.AMString = (string)a["am_string"]; done.Add("am_string"); }
                if (a["pm_string"] != null) { tf.PMString = (string)a["pm_string"]; done.Add("pm_string"); }
            }

            // Date-times
            if (Has(a, "datetime_order", "datetime_separator"))
            {
                var dtf = ff.DateTimeFormat ?? (ff.DateTimeFormat = new RD.DateTimeFieldFormat());
                if (a["datetime_order"] != null) { dtf.DateTimeOrder = Pick((string)a["datetime_order"], DateTimeOrderMap); done.Add("datetime_order"); }
                if (a["datetime_separator"] != null) { dtf.DateTimeSeparator = (string)a["datetime_separator"]; done.Add("datetime_separator"); }
            }

            if (a["boolean_output"] != null)
            {
                var bf = ff.BooleanFormat ?? (ff.BooleanFormat = new RD.BooleanFieldFormat());
                bf.OutputFormat = Pick((string)a["boolean_output"], BooleanMap);
                done.Add("boolean_output");
            }

            if (a["suppress_if_duplicated"] != null)
            {
                var cf = ff.CommonFormat ?? (ff.CommonFormat = new RD.CommonFieldFormat());
                cf.EnableSuppressIfDuplicated = (bool)a["suppress_if_duplicated"];
                done.Add("suppress_if_duplicated");
            }

            // Explicit formats only apply when the field stops using the Windows/system defaults.
            if (done.Any(d => (string)d != "suppress_if_duplicated"))
            {
                var cf = ff.CommonFormat ?? (ff.CommonFormat = new RD.CommonFieldFormat());
                cf.EnableSystemDefault = false;
            }
            return done;
        }

        private static JToken SetConditionFormula(JObject a) => Edit(a, main =>
        {
            var ras = Ras.For(main, Sub(a));
            var path = (string)a["condition"];
            var text = (string)a["formula"] ?? "";
            var obj = ras.Object((string)a["object"]);
            var clone = (RD.ISCRReportObject)obj.Clone(true);
            var set = Conditions.Set(clone, Ras.DeclaredType(obj), path, text);
            ras.ReportDef.ReportObjectController.Modify(obj, clone);
            return new JObject { ["object"] = obj.Name, ["condition"] = set, ["action"] = text.Length == 0 ? "cleared" : "set" };
        });

        /// <summary>Compact description of a field's explicit format, e.g. "decimals=2 thousands=',' date=dmy/".</summary>
        internal static string FormatText(RD.FieldFormat ff, string t)
        {
            if (ff == null) return null;
            var parts = new List<string>();
            if (ff.CommonFormat != null && ff.CommonFormat.EnableSystemDefault) parts.Add("system-default");
            t = t ?? "";
            var numeric = t.Contains("Number") || t.Contains("Currency") || t.Contains("Int") || t.Contains("Decimal");
            var date = t.Contains("Date");
            if (numeric && ff.NumericFormat is RD.NumericFieldFormat nf)
            {
                parts.Add($"decimals={nf.NDecimalPlaces}");
                parts.Add(nf.ThousandsSeparator ? $"thousands='{nf.ThousandSymbol}'" : "thousands=off");
                if (nf.DecimalSymbol != ".") parts.Add($"decimal='{nf.DecimalSymbol}'");
                if (nf.CurrencySymbolFormat != RD.CrCurrencySymbolTypeEnum.crCurrencySymbolTypeNoSymbol) parts.Add($"currency='{nf.CurrencySymbol}'");
                if (nf.EnableSuppressIfZero) parts.Add("suppress-if-zero");
            }
            if (date && ff.DateFormat is RD.DateFieldFormat df && df.SystemDefaultType == RD.CrDateSystemDefaultTypeEnum.crDateSystemDefaultTypeNotUsingDefaults)
            {
                parts.Add($"date={Key(DateOrderMap, df.DateOrder)} sep='{df.DateFirstSeparator}' y={Key(YearMap, df.YearFormat)} m={Key(MonthMap, df.MonthFormat)} d={Key(DayMap, df.DayFormat)}");
            }
            if ((t.Contains("Time")) && ff.TimeFormat is RD.TimeFieldFormat tf)
                parts.Add($"time={Key(TimeBaseMap, tf.TimeBase)}h sep='{tf.HourMinuteSeparator}'{(tf.SecondFormat == RD.CrSecondFormatEnum.crSecondFormatNoSecond ? " no-seconds" : "")}");
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }

        // ---------- option maps ----------

        private static bool Has(JObject a, params string[] keys) => keys.Any(k => a[k] != null);

        private static T Pick<T>(string value, Dictionary<string, T> map) =>
            map.TryGetValue((value ?? "").Trim().ToLowerInvariant(), out var v) ? v
                : throw new ToolError($"Invalid value '{value}'. Use one of: {string.Join(", ", map.Keys)}.");

        private static string Key<T>(Dictionary<string, T> map, T value) =>
            map.FirstOrDefault(kv => EqualityComparer<T>.Default.Equals(kv.Value, value)).Key ?? value.ToString();

        private static RD.CrRoundingTypeEnum RoundingFor(int decimals)
        {
            if (decimals < 0 || decimals > 10) throw new ToolError("rounding must be 0..10 decimals.");
            var order = new[]
            {
                RD.CrRoundingTypeEnum.crRoundingTypeRoundToUnit, RD.CrRoundingTypeEnum.crRoundingTypeRoundToTenth,
                RD.CrRoundingTypeEnum.crRoundingTypeRoundToHundredth, RD.CrRoundingTypeEnum.crRoundingTypeRoundToThousandth,
                RD.CrRoundingTypeEnum.crRoundingTypeRoundToTenThousandth, RD.CrRoundingTypeEnum.crRoundingTypeRoundToHundredThousandth,
                RD.CrRoundingTypeEnum.crRoundingTypeRoundToMillionth, RD.CrRoundingTypeEnum.crRoundingTypeRoundToTenMillionth,
                RD.CrRoundingTypeEnum.crRoundingTypeRoundToHundredMillionth, RD.CrRoundingTypeEnum.crRoundingTypeRoundToBillionth,
                RD.CrRoundingTypeEnum.crRoundingTypeRoundToTenBillionth
            };
            return order[decimals];
        }

        private static readonly Dictionary<string, RD.CrNegativeTypeEnum> NegativeMap = new Dictionary<string, RD.CrNegativeTypeEnum>
        {
            ["none"] = RD.CrNegativeTypeEnum.crNegativeTypeNotNegative, ["leading"] = RD.CrNegativeTypeEnum.crNegativeTypeLeadingMinus,
            ["trailing"] = RD.CrNegativeTypeEnum.crNegativeTypeTrailingMinus, ["brackets"] = RD.CrNegativeTypeEnum.crNegativeTypeBracketed,
        };
        private static readonly Dictionary<string, RD.CrCurrencySymbolTypeEnum> CurrencyMap = new Dictionary<string, RD.CrCurrencySymbolTypeEnum>
        {
            ["none"] = RD.CrCurrencySymbolTypeEnum.crCurrencySymbolTypeNoSymbol, ["fixed"] = RD.CrCurrencySymbolTypeEnum.crCurrencySymbolTypeFixedSymbol,
            ["floating"] = RD.CrCurrencySymbolTypeEnum.crCurrencySymbolTypeFloatingSymbol,
        };
        private static readonly Dictionary<string, RD.CrDateOrderEnum> DateOrderMap = new Dictionary<string, RD.CrDateOrderEnum>
        {
            ["ymd"] = RD.CrDateOrderEnum.crDateOrderYearMonthDay, ["dmy"] = RD.CrDateOrderEnum.crDateOrderDayMonthYear,
            ["mdy"] = RD.CrDateOrderEnum.crDateOrderMonthDayYear,
        };
        private static readonly Dictionary<string, RD.CrYearFormatEnum> YearMap = new Dictionary<string, RD.CrYearFormatEnum>
        {
            ["short"] = RD.CrYearFormatEnum.crYearFormatShortYear, ["long"] = RD.CrYearFormatEnum.crYearFormatLongYear,
            ["none"] = RD.CrYearFormatEnum.crYearFormatNoYear,
        };
        private static readonly Dictionary<string, RD.CrMonthFormatEnum> MonthMap = new Dictionary<string, RD.CrMonthFormatEnum>
        {
            ["numeric"] = RD.CrMonthFormatEnum.crMonthFormatNumericMonth, ["leading_zero"] = RD.CrMonthFormatEnum.crMonthFormatLeadingZeroNumericMonth,
            ["short"] = RD.CrMonthFormatEnum.crMonthFormatShortMonth, ["long"] = RD.CrMonthFormatEnum.crMonthFormatLongMonth,
            ["none"] = RD.CrMonthFormatEnum.crMonthFormatNoMonth,
        };
        private static readonly Dictionary<string, RD.CrDayFormatEnum> DayMap = new Dictionary<string, RD.CrDayFormatEnum>
        {
            ["numeric"] = RD.CrDayFormatEnum.crDayFormatNumericDay, ["leading_zero"] = RD.CrDayFormatEnum.crDayFormatLeadingZeroNumericDay,
            ["none"] = RD.CrDayFormatEnum.crDayFormatNoDay,
        };
        private static readonly Dictionary<string, RD.CrTimeBaseEnum> TimeBaseMap = new Dictionary<string, RD.CrTimeBaseEnum>
        {
            ["12"] = RD.CrTimeBaseEnum.crTimeBase12Hour, ["24"] = RD.CrTimeBaseEnum.crTimeBase24Hour,
        };
        private static readonly Dictionary<string, RD.CrDateTimeOrderEnum> DateTimeOrderMap = new Dictionary<string, RD.CrDateTimeOrderEnum>
        {
            ["date_time"] = RD.CrDateTimeOrderEnum.crDateTimeOrderDateThenTime, ["time_date"] = RD.CrDateTimeOrderEnum.crDateTimeOrderTimeThenDate,
            ["date"] = RD.CrDateTimeOrderEnum.crDateTimeOrderDateOnly, ["time"] = RD.CrDateTimeOrderEnum.crDateTimeOrderTimeOnly,
        };
        private static readonly Dictionary<string, RD.CrBooleanOutputFormatEnum> BooleanMap = new Dictionary<string, RD.CrBooleanOutputFormatEnum>
        {
            ["true_false"] = RD.CrBooleanOutputFormatEnum.crBooleanOutputFormatTrueOrFalse, ["t_f"] = RD.CrBooleanOutputFormatEnum.crBooleanOutputFormatTOrF,
            ["yes_no"] = RD.CrBooleanOutputFormatEnum.crBooleanOutputFormatYesOrNo, ["y_n"] = RD.CrBooleanOutputFormatEnum.crBooleanOutputFormatYOrN,
            ["1_0"] = RD.CrBooleanOutputFormatEnum.crBooleanOutputFormatOneOrZero,
        };
    }
}
