namespace NugetPackageServer

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Text
open System.Xml.Linq

type XmlDocEntry =
    {
        Summary: string option
        Params: (string * string) list
        Returns: string option
        Remarks: string option
    }

module XmlDocReader =

    let private trimDocText (text: string) =
        if isNull text then
            None
        else
            let trimmed =
                text.Split('\n')
                |> Array.map (fun l -> l.Trim())
                |> Array.filter (fun l -> l.Length > 0)
                |> String.concat " "

            if trimmed.Length > 0 then Some trimmed else None

    let private parseEntry (element: XElement) =
        let summary =
            element.Element(XName.Get "summary")
            |> Option.ofObj
            |> Option.bind (fun e -> trimDocText (e.Value))

        let parms =
            element.Elements(XName.Get "param")
            |> Seq.choose (fun e ->
                let name = e.Attribute(XName.Get "name")

                if isNull name then
                    None
                else
                    Some(name.Value, e.Value.Trim())
            )
            |> Seq.toList

        let returns =
            element.Element(XName.Get "returns")
            |> Option.ofObj
            |> Option.bind (fun e -> trimDocText (e.Value))

        let remarks =
            element.Element(XName.Get "remarks")
            |> Option.ofObj
            |> Option.bind (fun e -> trimDocText (e.Value))

        {
            Summary = summary
            Params = parms
            Returns = returns
            Remarks = remarks
        }

    let parseXmlDocFile (path: string) =
        let dict = Dictionary<string, XmlDocEntry>()

        try
            let doc = XDocument.Load(path)

            let members = doc.Descendants(XName.Get "member")

            for m in members do
                let nameAttr = m.Attribute(XName.Get "name")

                if not (isNull nameAttr) then
                    dict.[nameAttr.Value] <- parseEntry m
        with _ ->
            ()

        dict

    let rec private formatTypeName (t: Type) =
        if isNull t then
            ""
        elif t.IsGenericType then
            let baseName = t.GetGenericTypeDefinition().FullName
            let tick = baseName.IndexOf('`')
            let basePart = if tick >= 0 then baseName.Substring(0, tick) else baseName

            let args =
                t.GetGenericArguments()
                |> Array.map (fun a -> formatTypeName a)
                |> String.concat ","

            $"{basePart}{{{args}}}"
        elif t.IsArray then
            let elem = formatTypeName (t.GetElementType())
            let rank = t.GetArrayRank()

            if rank = 1 then
                $"{elem}[]"
            else
                let commas = String(',', rank - 1)
                $"{elem}[{commas}]"
        elif t.IsByRef then
            formatTypeName (t.GetElementType()) + "@"
        elif t.IsPointer then
            formatTypeName (t.GetElementType()) + "*"
        else
            match t.FullName with
            | null -> t.Name
            | name -> name

    let private formatParameters (parameters: ParameterInfo array) =
        let sb = StringBuilder()
        sb.Append("(") |> ignore

        for i in 0 .. parameters.Length - 1 do
            if i > 0 then
                sb.Append(",") |> ignore

            sb.Append(formatTypeName parameters.[i].ParameterType) |> ignore

        sb.Append(")") |> ignore
        sb.ToString()

    let buildMemberId (memberInfo: MemberInfo) =
        match memberInfo with
        | :? Type as t -> $"T:{t.FullName}"
        | :? MethodInfo as m ->
            let parms = m.GetParameters()

            let parmStr = if parms.Length = 0 then "" else formatParameters parms

            $"M:{m.DeclaringType.FullName}.{m.Name}{parmStr}"
        | :? ConstructorInfo as c ->
            let parms = c.GetParameters()

            let parmStr = if parms.Length = 0 then "" else formatParameters parms

            $"M:{c.DeclaringType.FullName}.#ctor{parmStr}"
        | :? PropertyInfo as p -> $"P:{p.DeclaringType.FullName}.{p.Name}"
        | :? FieldInfo as f -> $"F:{f.DeclaringType.FullName}.{f.Name}"
        | :? EventInfo as e -> $"E:{e.DeclaringType.FullName}.{e.Name}"
        | _ -> ""
