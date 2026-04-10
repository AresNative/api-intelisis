// Services/QueryBuilder.cs
using Microsoft.Data.SqlClient;
using MyApiProject.Models;
using System.Data;
using System.Text;
using System.Text.Json;

namespace MyApiProject.Services
{
    /// <summary>
    /// Construye dinámicamente las partes SQL de un SELECT paginado a partir de <see cref="FiltrosRequest"/>.
    ///
    /// Responsabilidades:
    ///   BuildSelect     → SELECT clause + GROUP BY clause (corrige el bug de columnas compartidas)
    ///   BuildWhere      → WHERE clause + parámetros (procesa Filtros, FiltrosAnd Y FiltrosOr)
    ///   BuildOrderBy    → ORDER BY clause
    ///   BuildPaginated  → query paginada con ROW_NUMBER según estrategia
    ///   BuildCount      → query de conteo total
    /// </summary>
    public class QueryBuilder
    {
        // ── Whitelists de seguridad ────────────────────────────────────────────
        private static readonly HashSet<string> AllowedOperators = new(StringComparer.OrdinalIgnoreCase)
        {
            "=", "!=", "<>", ">", ">=", "<", "<=",
            "LIKE", "NOT LIKE",
            "IN", "NOT IN",
            "BETWEEN", "NOT BETWEEN",
            "IS NULL", "IS NOT NULL",
            "TIME_BETWEEN", "CASE_WHEN"
        };

        private static readonly HashSet<string> AllowedAggregations = new(StringComparer.OrdinalIgnoreCase)
        {
            "SUM", "COUNT", "AVG", "MIN", "MAX", "DISTINCT", "COUNT DISTINCT"
        };

        // ── 1. SELECT + GROUP BY ───────────────────────────────────────────────

        /// <summary>
        /// Construye SELECT y GROUP BY respetando la regla fundamental:
        ///
        ///   • Columnas de Selects  → van en SELECT y en GROUP BY
        ///   • Columnas de Agregaciones → van en SELECT como función de agregado,
        ///     NUNCA en GROUP BY, aunque la misma columna aparezca también en Selects.
        ///
        /// Casos especiales:
        ///   • DISTINCT sin agregaciones  → SELECT DISTINCT ..., sin GROUP BY
        ///   • COUNT DISTINCT             → COUNT(DISTINCT col)
        ///   • Expresiones CASE WHEN      → se excluyen del GROUP BY
        /// </summary>
        public (string SelectClause, string GroupByClause, bool HasDistinct) BuildSelect(FiltrosRequest request)
        {
            var selectParts = new List<string>();
            var groupByParts = new List<string>();

            bool hasAggregations = false;
            bool hasDistinctOnly = false;

            // ── Columnas planas del SELECT ─────────────────────────────────
            // TODAS van al GROUP BY si hay agregaciones — excepto expresiones complejas.
            // Las Agregaciones (MIN, MAX, SUM, etc.) NUNCA van al GROUP BY,
            // aunque la misma columna también esté en Selects: SQL Server lo permite
            // y calcula la función dentro de cada grupo formado por el GROUP BY.
            foreach (var s in request.Selects.Where(s => !string.IsNullOrWhiteSpace(s.Key)))
            {
                var col = FormatColumnExpression(s.Key);
                var alias = !string.IsNullOrWhiteSpace(s.Alias) ? $" AS [{s.Alias}]" : "";
                selectParts.Add($"{col}{alias}");

                // Solo columnas simples van al GROUP BY — no expresiones matemáticas/funciones
                if (!IsComplexExpression(s.Key))
                    groupByParts.Add(col);
            }

            // ── Agregaciones ───────────────────────────────────────────────
            foreach (var agg in request.Agregaciones.Where(a => !string.IsNullOrWhiteSpace(a.Key)))
            {
                var op = NormalizeAggregation(agg.Operation);
                var col = FormatColumnExpression(agg.Key);
                var alias = !string.IsNullOrWhiteSpace(agg.Alias)
                    ? $" AS [{agg.Alias}]"
                    : $" AS [{op.Replace(" ", "_")}_{agg.Key.Replace(".", "_")}]";

                switch (op)
                {
                    case "DISTINCT":
                        // DISTINCT no es una función de agregado — no genera GROUP BY
                        // y no cambia hasAggregations
                        selectParts.Add($"{col}{alias}");
                        hasDistinctOnly = true;
                        break;

                    case "COUNT DISTINCT":
                        selectParts.Add($"COUNT(DISTINCT {col}){alias}");
                        hasAggregations = true;
                        // ✅ NO va al GROUP BY
                        break;

                    default:
                        selectParts.Add($"{op}({col}){alias}");
                        hasAggregations = true;
                        // ✅ NO va al GROUP BY
                        break;
                }
            }

            if (!selectParts.Any())
                selectParts.Add("*");

            // ── Construir SELECT final ─────────────────────────────────────
            // NUNCA se pone DISTINCT como prefijo aquí — se propaga como flag HasDistinct
            // para que BuildPaginatedQuery lo maneje correctamente en la subconsulta.
            // Mezclar DISTINCT con ROW_NUMBER() en el mismo SELECT es inválido en SQL Server.
            string selectClause = string.Join(", ", selectParts);

            // ── GROUP BY solo cuando hay funciones de agregado ─────────────
            // y hay columnas planas que agrupar; las expresiones complejas se excluyen
            string groupByClause = string.Empty;
            if (hasAggregations && groupByParts.Any())
            {
                var validGroupBy = groupByParts
                    .Where(g => !IsComplexExpression(g))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (validGroupBy.Any())
                    groupByClause = $"GROUP BY {string.Join(", ", validGroupBy)}";
            }

            // hasDistinct: hay DISTINCT puro sin agregaciones → la query externa debe
            // envolver en subconsulta para poder añadir ROW_NUMBER sin conflicto
            bool hasDistinct = hasDistinctOnly && !hasAggregations;

            return (selectClause, groupByClause, hasDistinct);
        }

        // ── 2. WHERE ───────────────────────────────────────────────────────────

        /// <summary>
        /// Construye el WHERE procesando los tres niveles:
        ///   1. Filtros planos → AND entre sí
        ///   2. FiltrosAnd    → cada grupo usa su OperadorLogico interno; grupos entre sí con AND
        ///   3. FiltrosOr     → igual que FiltrosAnd (mismo tipo, mismo procesamiento)
        ///
        /// Todos los niveles se unen con AND al WHERE final.
        /// </summary>
        public string BuildWhere(FiltrosRequest request, List<SqlParameter> parameters)
        {
            var topClauses = new List<string>();
            int paramCounter = 0;

            // ── Filtros planos ─────────────────────────────────────────────
            foreach (var f in request.Filtros.Where(f => !string.IsNullOrWhiteSpace(f.Key)))
            {
                var clause = BuildSingleFilter(f, parameters, ref paramCounter);
                if (clause != null) topClauses.Add(clause);
            }

            // ── FiltrosAnd y FiltrosOr (mismo procesamiento) ───────────────
            var todosLosGrupos = (request.FiltrosAnd ?? new())
                .Concat(request.FiltrosOr ?? new());

            foreach (var grupo in todosLosGrupos)
            {
                var validItems = grupo.Filtros
                    .Where(f => !string.IsNullOrWhiteSpace(f.Key))
                    .ToList();

                if (!validItems.Any()) continue;

                var groupClauses = new List<string>();
                foreach (var f in validItems)
                {
                    var clause = BuildSingleFilter(f, parameters, ref paramCounter);
                    if (clause != null) groupClauses.Add(clause);
                }

                if (!groupClauses.Any()) continue;

                var separator = (grupo.OperadorLogico?.ToUpperInvariant() == "OR") ? " OR " : " AND ";

                topClauses.Add(groupClauses.Count == 1
                    ? groupClauses[0]
                    : $"({string.Join(separator, groupClauses)})");
            }

            return topClauses.Any()
                ? $"WHERE {string.Join("\n  AND ", topClauses)}"
                : string.Empty;
        }

        // ── 2b. HAVING ────────────────────────────────────────────────────────

        /// <summary>
        /// Construye el HAVING para filtrar sobre valores agregados.
        /// Los filtros referencian aliases de agregaciones (ej: totalCosto, minimoCosto).
        ///
        /// IMPORTANTE: el HAVING usa los aliases del SELECT, no expresiones de agregado.
        /// SQL Server permite referenciar aliases de agregaciones en el HAVING cuando
        /// la query está envuelta en un CTE (el patrón _Grouped → _Page que usamos).
        /// Para la query directa (sin CTE), se usa la expresión original de la agregación.
        ///
        /// Los parámetros SQL se agregan a la misma lista que el WHERE.
        /// </summary>
        public string BuildHaving(FiltrosRequest request, List<SqlParameter> parameters,
            bool useCteAliases = true)
        {
            if (request.Having == null || !request.Having.Any(h => !string.IsNullOrWhiteSpace(h.Key)))
                return string.Empty;

            // Mapa alias → expresión original para cuando no podemos usar alias
            var aliasToExpr = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var agg in request.Agregaciones.Where(a => !string.IsNullOrWhiteSpace(a.Alias)))
            {
                var op = NormalizeAggregation(agg.Operation);
                var col = FormatColumnExpression(agg.Key);
                var expr = op == "COUNT DISTINCT" ? $"COUNT(DISTINCT {col})" : $"{op}({col})";
                aliasToExpr[agg.Alias!] = expr;
            }

            var havingClauses = new List<string>();
            int counter = 0;

            foreach (var h in request.Having.Where(h => !string.IsNullOrWhiteSpace(h.Key)))
            {
                // Resolver la columna: si es alias de agregación, usar alias o expresión
                string column;
                if (aliasToExpr.ContainsKey(h.Key))
                    column = useCteAliases ? $"[{h.Key}]" : aliasToExpr[h.Key];
                else
                    column = FormatColumnName(h.Key);

                var op = NormalizeOperator(h.Operator) ?? "=";

                if (op is "IS NULL" or "IS NOT NULL")
                {
                    havingClauses.Add($"{column} {op}");
                    continue;
                }

                var pName = $"@h{counter++}";
                parameters.Add(CreateTypedParameter(pName, h.Value));
                havingClauses.Add($"{column} {op} {pName}");
            }

            return havingClauses.Any()
                ? $"HAVING {string.Join(" AND ", havingClauses)}"
                : string.Empty;
        }

        // ── 3. ORDER BY ────────────────────────────────────────────────────────

        public string BuildOrderBy(FiltrosRequest request)
        {
            var parts = request.Order
                .Where(o => !string.IsNullOrWhiteSpace(o.Key))
                .Select(o =>
                {
                    var dir = o.Direction?.ToUpperInvariant() == "DESC" ? "DESC" : "ASC";
                    var col = FindColumnAlias(o.Key, request) ?? FormatColumnName(o.Key);
                    return $"{col} {dir}";
                })
                .ToList();

            return parts.Any() ? $"ORDER BY {string.Join(", ", parts)}" : string.Empty;
        }

        // ── 4. Queries paginadas ────────────────────────────────────────────────

        /// <summary>
        /// Construye la query paginada usando ROW_NUMBER (estrategias Direct y CTE son idénticas en SQL Server moderno).
        /// Para DISTINCT en el SELECT se usa una subconsulta para evitar conflicto con ROW_NUMBER.
        /// </summary>
        public string BuildPaginatedQuery(
            string selectClause,
            string fromClause,
            string whereClause,
            string groupByClause,
            string orderByExpression,  // sin el "ORDER BY" inicial
            int offset,
            int pageSize,
            bool hasDistinct = false,
            string havingClause = "")
        {
            if (hasDistinct)
            {
                // DISTINCT + ROW_NUMBER no pueden convivir en el mismo SELECT.
                // Solución: ROW_NUMBER en la subconsulta interna (sin DISTINCT),
                // y SELECT DISTINCT en la consulta externa sobre los resultados paginados.
                return $@"
SELECT DISTINCT {selectClause}
FROM (
    SELECT {selectClause},
           ROW_NUMBER() OVER (ORDER BY {orderByExpression}) AS _RowNum
    FROM {fromClause}
    {whereClause}
    {groupByClause}
    {havingClause}
) AS _Numbered
WHERE _RowNum > @_Offset AND _RowNum <= @_Offset + @_PageSize";
            }

            if (!string.IsNullOrWhiteSpace(groupByClause))
            {
                return $@"
WITH _Grouped AS (
    SELECT {selectClause}
    FROM {fromClause}
    {whereClause}
    {groupByClause}
    {havingClause}
),
_Page AS (
    SELECT *, ROW_NUMBER() OVER (ORDER BY {orderByExpression}) AS _RowNum
    FROM _Grouped
)
SELECT * FROM _Page
WHERE _RowNum > @_Offset AND _RowNum <= @_Offset + @_PageSize
ORDER BY _RowNum";
            }

            return $@"
WITH _Page AS (
    SELECT {selectClause},
           ROW_NUMBER() OVER (ORDER BY {orderByExpression}) AS _RowNum
    FROM {fromClause}
    {whereClause}
    {havingClause}
)
SELECT * FROM _Page
WHERE _RowNum > @_Offset AND _RowNum <= @_Offset + @_PageSize
ORDER BY _RowNum";
        }

        /// <summary>
        /// Tabla temporal para GROUP BY complejos (muchas columnas / muchos parámetros).
        /// Más eficiente en estas situaciones porque SQL Server materializa el resultado una sola vez.
        /// </summary>
        public string BuildTempTableQuery(
            string selectClause,
            string fromClause,
            string whereClause,
            string groupByClause,
            string tempTableName,
            int rowLimit,
            string havingClause = "")
        {
            return $@"
SELECT TOP ({rowLimit}) {selectClause}
INTO {tempTableName}
FROM {fromClause}
{whereClause}
{groupByClause}
{havingClause}";
        }

        /// <summary>
        /// Query keyset: pagina usando un valor anchor en lugar de OFFSET.
        /// Requiere un ORDER BY definido y funciona mejor cuando el cliente
        /// sigue la secuencia sin saltar páginas.
        /// </summary>
        public string BuildKeysetQuery(
            string selectClause,
            string fromClause,
            string whereClause,
            string groupByClause,
            string orderByExpression,
            string direction,   // ">" o "<"
            string anchorColumn,
            int pageSize)
        {
            var anchorCondition = $"{anchorColumn} {direction} @_Anchor";
            var combinedWhere = string.IsNullOrWhiteSpace(whereClause)
                ? $"WHERE {anchorCondition}"
                : $"{whereClause}\n  AND {anchorCondition}";

            return $@"
SELECT TOP ({pageSize}) {selectClause}
FROM {fromClause}
{combinedWhere}
{groupByClause}
ORDER BY {orderByExpression}";
        }

        // ── 5. Conteo total ────────────────────────────────────────────────────

        public string BuildCountQuery(
            string fromClause,
            string whereClause,
            string groupByClause,
            string havingClause = "")
        {
            if (string.IsNullOrWhiteSpace(groupByClause) && string.IsNullOrWhiteSpace(havingClause))
            {
                return $@"
SELECT COUNT_BIG(*) AS _Total
FROM {fromClause}
{whereClause}";
            }

            // Contar los grupos distintos resultantes.
            // SQL Server requiere alias en todas las columnas de subquery (error 8155).
            return $@"
SELECT COUNT_BIG(*) AS _Total
FROM (
    SELECT 1 AS _GroupRow
    FROM {fromClause}
    {whereClause}
    {groupByClause}
    {havingClause}
) AS _CountSource";
        }

        // ── Utilidades de formateo (public para uso en BaseController) ─────────

        public string FormatColumnName(string column)
        {
            if (string.IsNullOrWhiteSpace(column)) return column;
            if (IsComplexExpression(column)) return column;

            // Si ya tiene corchetes, devolver tal cual
            if (column.Contains('[')) return column;

            // Columna calificada con alias de tabla: alias.columna
            // SQL Server resuelve el alias del JOIN correctamente sin corchetes.
            // Agregar [alias].[columna] hace que el motor lo interprete como
            // esquema.tabla en lugar de alias.columna y falla el binding.
            if (column.Contains('.'))
                return column;

            // Identificador simple sin punto → corchetes para escapar palabras reservadas
            return $"[{column}]";
        }

        public string FormatColumnExpression(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression)) return expression;

            // Expresiones matemáticas entre paréntesis
            if ((expression.Contains('*') || expression.Contains('/') ||
                 expression.Contains('+') || expression.Contains('-')) &&
                !expression.StartsWith("CASE", StringComparison.OrdinalIgnoreCase))
            {
                if (!expression.StartsWith("(") || !expression.EndsWith(")"))
                    return $"({expression})";
                return expression;
            }

            return FormatColumnName(expression);
        }

        public bool IsComplexExpression(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression)) return false;

            var trimmed = expression.TrimStart();

            // CASE WHEN puede no tener paréntesis propios — detectar por keyword
            if (trimmed.StartsWith("CASE", StringComparison.OrdinalIgnoreCase))
                return true;

            // Expresiones con funciones SQL o matemáticas (requieren paréntesis)
            return (expression.Contains('(') && expression.Contains(')')) &&
                   (expression.Contains('*') || expression.Contains('/') ||
                    expression.Contains('+') || expression.Contains('-') ||
                    expression.Contains("CONVERT", StringComparison.OrdinalIgnoreCase) ||
                    expression.Contains("CAST", StringComparison.OrdinalIgnoreCase));
        }

        public SqlParameter CreateTypedParameter(string name, string value)
        {
            if (int.TryParse(value, out int intValue))
                return new SqlParameter(name, SqlDbType.Int) { Value = intValue };

            if (decimal.TryParse(value, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out decimal decimalValue))
                return new SqlParameter(name, SqlDbType.Decimal) { Value = decimalValue };

            if (DateTime.TryParse(value, out DateTime dateValue))
                return new SqlParameter(name, SqlDbType.DateTime) { Value = dateValue };

            if (bool.TryParse(value, out bool boolValue))
                return new SqlParameter(name, SqlDbType.Bit) { Value = boolValue };

            return new SqlParameter(name, SqlDbType.NVarChar, Math.Min(value.Length, 4000))
            { Value = value };
        }

        /// <summary>
        /// Copia parámetros a un SqlCommand sin duplicar nombres.
        /// Preserva tipo, precision y scale.
        /// </summary>
        public void AddParametersTo(SqlCommand command, IEnumerable<SqlParameter> parameters)
        {
            foreach (var p in parameters)
            {
                if (command.Parameters.Contains(p.ParameterName)) continue;

                var added = command.Parameters.Add(p.ParameterName, p.SqlDbType, p.Size);
                added.Value = p.Value ?? DBNull.Value;
                if (p.Precision > 0) added.Precision = p.Precision;
                if (p.Scale > 0) added.Scale = p.Scale;
            }
        }

        public string[] GetFieldNames(SqlDataReader reader)
        {
            var names = new string[reader.FieldCount];
            for (int i = 0; i < reader.FieldCount; i++)
                names[i] = reader.GetName(i);
            return names;
        }

        public int CalculateDynamicTimeout(string query, int paramCount)
        {
            int timeout = 30;
            if (query.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase)) timeout += 15;
            if (query.Contains("JOIN", StringComparison.OrdinalIgnoreCase)) timeout += 10;
            if (query.Contains("WITH ", StringComparison.OrdinalIgnoreCase)) timeout += 10;
            if (paramCount > 5) timeout += 5;
            return Math.Min(timeout, 120);
        }

        // ── Extracción de alias / columnas ─────────────────────────────────────

        public string ExtractFirstOrderColumn(string orderByExpression)
        {
            if (string.IsNullOrWhiteSpace(orderByExpression)) return string.Empty;

            return orderByExpression
                .Replace("ORDER BY", "", StringComparison.OrdinalIgnoreCase)
                .Split(',')[0]
                .Trim()
                .Split(' ')[0];
        }

        public string ExtractFirstGroupColumn(string groupByClause)
        {
            if (string.IsNullOrWhiteSpace(groupByClause)) return string.Empty;

            return groupByClause
                .Replace("GROUP BY", "", StringComparison.OrdinalIgnoreCase)
                .Split(',')[0]
                .Trim();
        }

        /// <summary>
        /// Extrae primera columna o alias del SELECT para usar como ORDER BY fallback.
        /// Prefiere el alias si existe.
        /// </summary>
        public string ExtractFirstSelectColumnOrAlias(string selectClause)
        {
            if (string.IsNullOrWhiteSpace(selectClause) || selectClause.Trim() == "*")
                return "(SELECT NULL)";

            var first = SplitSelectClause(selectClause).FirstOrDefault()?.Trim() ?? string.Empty;

            // Si tiene alias, retornar el alias entre corchetes
            // (los corchetes son obligatorios cuando el alias contiene espacios)
            var asIndex = first.IndexOf(" AS ", StringComparison.OrdinalIgnoreCase);
            if (asIndex > 0)
            {
                var alias = first[(asIndex + 4)..].Trim().Trim('[', ']');
                return $"[{alias}]";
            }

            // Columna calificada alias.columna — devolver completa para evitar ambigüedad.
            // Ejemplo: "CB.Codigo" → "CB.Codigo" (NO solo "Codigo", ambiguo en JOINs)
            if (first.Contains('.') && !IsComplexExpression(first))
                return first;

            return IsComplexExpression(first) ? "(SELECT NULL)" : first;
        }

        public List<string> SplitSelectClause(string selectClause)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            int parenCount = 0;

            foreach (char c in selectClause)
            {
                if (c == '(') parenCount++;
                else if (c == ')') parenCount--;

                if (c == ',' && parenCount == 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            if (current.Length > 0) result.Add(current.ToString());
            return result;
        }

        // ── Helpers privados ───────────────────────────────────────────────────

        private string? BuildSingleFilter(BusquedaParams f, List<SqlParameter> parameters, ref int counter)
        {
            var op = NormalizeOperator(f.Operator);
            if (op == null) return null;

            var column = FormatFilterColumn(f.Key);

            switch (op)
            {
                case "IS NULL":
                case "IS NOT NULL":
                    return $"{column} {op}";

                case "CASE_WHEN":
                    return BuildCaseWhenClause(f, parameters, ref counter);

                case "TIME_BETWEEN":
                    return BuildTimeBetweenClause(column, f.Value, parameters, ref counter);

                case "BETWEEN":
                case "NOT BETWEEN":
                    return BuildBetweenClause(column, op, f.Value, parameters, ref counter);

                case "IN":
                case "NOT IN":
                    return BuildInClause(column, op, f.Value, parameters, ref counter);

                case "LIKE":
                case "NOT LIKE":
                    {
                        var pName = NextParam(ref counter);
                        var pValue = f.Value.StartsWith("%") || f.Value.EndsWith("%")
                            ? f.Value
                            : $"%{f.Value}%";
                        parameters.Add(new SqlParameter(pName, SqlDbType.NVarChar, 4000) { Value = pValue });
                        return $"{column} {op} {pName}";
                    }

                default:
                    {
                        var pName = NextParam(ref counter);
                        parameters.Add(CreateTypedParameter(pName, f.Value));
                        return $"{column} {op} {pName}";
                    }
            }
        }

        private string FormatFilterColumn(string column)
        {
            if (string.IsNullOrWhiteSpace(column)) return column;

            // Permitir expresiones de filtro (funciones, operadores)
            if (column.Contains('(') || column.Contains('*') ||
                column.Contains('/') || column.Contains('+'))
                return column;

            return FormatColumnName(column);
        }

        private string? BuildInClause(string column, string op, string value,
            List<SqlParameter> parameters, ref int counter)
        {
            var values = value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct()
                .ToList();

            if (!values.Any()) return null;

            // Optimización: un solo valor → igualdad simple
            if (values.Count == 1)
            {
                var pName = NextParam(ref counter);
                var singleOp = op == "IN" ? "=" : "<>";
                parameters.Add(CreateTypedParameter(pName, values[0]));
                return $"{column} {singleOp} {pName}";
            }

            var paramNames = new List<string>();
            foreach (var v in values)
            {
                var pn = NextParam(ref counter);
                parameters.Add(CreateTypedParameter(pn, v));
                paramNames.Add(pn);
            }

            return $"{column} {op} ({string.Join(", ", paramNames)})";
        }

        private string BuildBetweenClause(string column, string op, string value,
            List<SqlParameter> parameters, ref int counter)
        {
            var parts = value.Split(new[] { " AND ", " and " }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return "1 = 1";

            var from = NextParam(ref counter);
            var to = NextParam(ref counter);
            parameters.Add(CreateTypedParameter(from, parts[0].Trim()));
            parameters.Add(CreateTypedParameter(to, parts[1].Trim()));
            return $"{column} {op} {from} AND {to}";
        }

        private string BuildTimeBetweenClause(string column, string value,
            List<SqlParameter> parameters, ref int counter)
        {
            var parts = value.Split(new[] { " AND ", " and " }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return "1 = 1";

            var from = NextParam(ref counter);
            var to = NextParam(ref counter);
            parameters.Add(CreateTimeParameter(from, parts[0].Trim()));
            parameters.Add(CreateTimeParameter(to, parts[1].Trim()));
            return $"CAST({column} AS TIME) BETWEEN {from} AND {to}";
        }

        private string? BuildCaseWhenClause(BusquedaParams f,
            List<SqlParameter> parameters, ref int counter)
        {
            try
            {
                var caseExpr = BuildCaseExpression(f.Value, parameters, ref counter);
                if (string.IsNullOrEmpty(caseExpr)) return null;

                return !string.IsNullOrWhiteSpace(f.Key)
                    ? $"{caseExpr} = 1"
                    : caseExpr;
            }
            catch
            {
                return "1 = 1"; // Fallback seguro
            }
        }

        private string BuildCaseExpression(string value, List<SqlParameter> parameters, ref int counter)
        {
            // Si ya es una expresión CASE WHEN completa, usarla directamente
            if (value.Trim().StartsWith("CASE", StringComparison.OrdinalIgnoreCase))
                return value;

            try
            {
                var jsonDoc = JsonDocument.Parse(value);
                var root = jsonDoc.RootElement;
                var sb = new StringBuilder("CASE");

                if (root.TryGetProperty("when", out var whenArray))
                    foreach (var item in whenArray.EnumerateArray())
                        if (item.TryGetProperty("condition", out var cond) &&
                            item.TryGetProperty("then", out var then))
                        {
                            var parsedCond = ParseCondition(cond.GetString() ?? "", parameters, ref counter);
                            var parsedThen = ParseValue(then.GetString() ?? "", parameters, ref counter);
                            sb.Append($" WHEN {parsedCond} THEN {parsedThen}");
                        }

                if (root.TryGetProperty("else", out var elseVal))
                    sb.Append($" ELSE {ParseValue(elseVal.GetString() ?? "NULL", parameters, ref counter)}");

                sb.Append(" END");
                return sb.ToString();
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }

        private string ParseCondition(string condition, List<SqlParameter> parameters, ref int counter)
        {
            if (string.IsNullOrEmpty(condition)) return "1 = 1";

            var operators = new[] { ">=", "<=", "!=", "<>", ">", "<", "=", "LIKE", "IN", "BETWEEN" };
            foreach (var op in operators)
            {
                var idx = condition.IndexOf($" {op} ", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;

                var left = condition[..idx].Trim();
                var right = condition[(idx + op.Length + 2)..].Trim();

                // Literales: no necesitan parámetro
                if ((right.StartsWith("'") && right.EndsWith("'")) ||
                    int.TryParse(right, out _) || decimal.TryParse(right, out _) ||
                    right.Equals("NULL", StringComparison.OrdinalIgnoreCase))
                    return $"{left} {op} {right}";

                var pName = NextParam(ref counter);
                parameters.Add(CreateTypedParameter(pName, right));
                return $"{left} {op} {pName}";
            }

            return condition;
        }

        private string ParseValue(string value, List<SqlParameter> parameters, ref int counter)
        {
            if (string.IsNullOrEmpty(value) || value.Equals("NULL", StringComparison.OrdinalIgnoreCase))
                return "NULL";

            if ((value.StartsWith("'") && value.EndsWith("'")) ||
                int.TryParse(value, out _) || decimal.TryParse(value, out _))
                return value;

            var pName = NextParam(ref counter);
            parameters.Add(CreateTypedParameter(pName, value));
            return pName;
        }

        private SqlParameter CreateTimeParameter(string name, string value)
        {
            if (TimeSpan.TryParse(value, out var ts))
                return new SqlParameter(name, SqlDbType.Time) { Value = ts };

            if (DateTime.TryParse(value, out var dt))
                return new SqlParameter(name, SqlDbType.Time) { Value = dt.TimeOfDay };

            return new SqlParameter(name, SqlDbType.Time) { Value = TimeSpan.Zero };
        }

        private string? FindColumnAlias(string column, FiltrosRequest request)
        {
            var agg = request.Agregaciones?.FirstOrDefault(a => a.Key == column || a.Alias == column);
            if (agg?.Alias != null) return $"[{agg.Alias}]";

            var sel = request.Selects?.FirstOrDefault(s => s.Key == column || s.Alias == column);
            if (sel?.Alias != null) return $"[{sel.Alias}]";

            return null;
        }

        private static string NextParam(ref int counter) => $"@p{counter++}";

        private static string? NormalizeOperator(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "=";
            var upper = input.Trim().ToUpperInvariant();
            return AllowedOperators.Contains(upper) ? upper : null;
        }

        private static string NormalizeAggregation(string? op)
        {
            if (string.IsNullOrWhiteSpace(op)) return "SUM";
            var upper = op.Trim().ToUpperInvariant();

            // Normalizar variantes de COUNT DISTINCT
            if (upper.Contains("COUNT") && upper.Contains("DISTINCT")) return "COUNT DISTINCT";

            return AllowedAggregations.Contains(upper) ? upper : "SUM";
        }
    }
}