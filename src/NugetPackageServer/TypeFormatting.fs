namespace NugetPackageServer

open System
open System.Reflection
open System.Text

module TypeFormatting =

    let rec formatTypeName (t: Type) =
        if isNull t then
            "void"
        elif t.IsGenericParameter then
            t.Name
        elif t.IsGenericType then
            let def = t.GetGenericTypeDefinition()

            let baseName =
                match def.FullName with
                | null -> def.Name
                | name -> name

            let tick = baseName.IndexOf('`')
            let basePart = if tick >= 0 then baseName.Substring(0, tick) else baseName

            let args = t.GetGenericArguments() |> Array.map formatTypeName |> String.concat ", "

            $"{basePart}<{args}>"
        elif t.IsArray then
            let elem = formatTypeName (t.GetElementType())
            let rank = t.GetArrayRank()

            if rank = 1 then
                $"{elem}[]"
            else
                let commas = String(',', rank - 1)
                $"{elem}[{commas}]"
        elif t.IsByRef then
            $"ref {formatTypeName (t.GetElementType())}"
        elif t.IsPointer then
            $"{formatTypeName (t.GetElementType())}*"
        else
            match t.FullName with
            | null -> t.Name
            | "System.Void" -> "void"
            | "System.String" -> "string"
            | "System.Int32" -> "int"
            | "System.Int64" -> "long"
            | "System.Boolean" -> "bool"
            | "System.Double" -> "double"
            | "System.Single" -> "float"
            | "System.Decimal" -> "decimal"
            | "System.Byte" -> "byte"
            | "System.Object" -> "object"
            | name -> name

    let formatShortTypeName (t: Type) =
        if isNull t then
            "void"
        elif t.IsGenericType then
            let tick = t.Name.IndexOf('`')
            let basePart = if tick >= 0 then t.Name.Substring(0, tick) else t.Name

            let args =
                t.GetGenericArguments() |> Array.map (fun a -> a.Name) |> String.concat ", "

            $"{basePart}<{args}>"
        else
            t.Name

    let private formatAccessibility (m: MethodBase) =
        if m.IsPublic then "public"
        elif m.IsFamily then "protected"
        elif m.IsFamilyOrAssembly then "protected internal"
        elif m.IsAssembly then "internal"
        else "private"

    let private formatParameters (parms: ParameterInfo array) =
        parms
        |> Array.map (fun p ->
            let typeName = formatTypeName p.ParameterType
            $"{typeName} {p.Name}"
        )
        |> String.concat ", "

    let formatMethod (m: MethodInfo) =
        let sb = StringBuilder()
        sb.Append(formatAccessibility m) |> ignore

        if m.IsStatic then
            sb.Append(" static") |> ignore

        if m.IsAbstract then
            sb.Append(" abstract") |> ignore
        elif m.IsVirtual && not m.IsFinal then
            sb.Append(" virtual") |> ignore

        sb.Append($" {formatTypeName m.ReturnType}") |> ignore

        let name =
            if m.IsGenericMethod then
                let args =
                    m.GetGenericArguments() |> Array.map (fun a -> a.Name) |> String.concat ", "

                $"{m.Name}<{args}>"
            else
                m.Name

        sb.Append($" {name}({formatParameters (m.GetParameters())})") |> ignore
        sb.ToString()

    let formatConstructor (c: ConstructorInfo) =
        let sb = StringBuilder()
        sb.Append(formatAccessibility c) |> ignore

        if c.IsStatic then
            sb.Append(" static") |> ignore

        let typeName = formatShortTypeName c.DeclaringType
        sb.Append($" {typeName}({formatParameters (c.GetParameters())})") |> ignore
        sb.ToString()

    let formatProperty (p: PropertyInfo) =
        let sb = StringBuilder()

        let getter = p.GetGetMethod(true)
        let setter = p.GetSetMethod(true)

        let accessMethod =
            if not (isNull getter) then getter
            elif not (isNull setter) then setter
            else null

        if not (isNull accessMethod) then
            sb.Append(formatAccessibility accessMethod) |> ignore

            if accessMethod.IsStatic then
                sb.Append(" static") |> ignore

        sb.Append($" {formatTypeName p.PropertyType} {p.Name}") |> ignore
        sb.Append(" { ") |> ignore

        if not (isNull getter) then
            sb.Append("get; ") |> ignore

        if not (isNull setter) then
            sb.Append("set; ") |> ignore

        sb.Append("}") |> ignore
        sb.ToString()

    let formatEvent (e: EventInfo) =
        $"event {formatTypeName e.EventHandlerType} {e.Name}"

    let formatField (f: FieldInfo) =
        let sb = StringBuilder()

        if f.IsPublic then sb.Append("public") |> ignore
        elif f.IsFamily then sb.Append("protected") |> ignore
        else sb.Append("internal") |> ignore

        if f.IsStatic then
            sb.Append(" static") |> ignore

        if f.IsLiteral then
            sb.Append(" const") |> ignore
        elif f.IsInitOnly then
            sb.Append(" readonly") |> ignore

        sb.Append($" {formatTypeName f.FieldType} {f.Name}") |> ignore
        sb.ToString()

    let formatTypeKind (t: Type) =
        if t.IsInterface then
            "interface"
        elif t.IsEnum then
            "enum"
        elif t.IsValueType then
            "struct"
        elif t.BaseType |> isNull |> not && t.BaseType.FullName = "System.MulticastDelegate" then
            "delegate"
        elif t.IsAbstract && t.IsSealed then
            "static class"
        elif t.IsAbstract then
            "abstract class"
        elif t.IsSealed then
            "sealed class"
        else
            "class"
