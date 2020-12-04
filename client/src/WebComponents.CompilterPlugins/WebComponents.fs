﻿namespace WebComponent

open Fable
open Fable.AST
open Fable.AST.Fable
    
    
// Tell Fable to scan for plugins in this assembly
[<assembly:ScanForPlugins>]
do()
    
/// <summary>Transforms a function into a React function component. Make sure the function is defined at the module level</summary>
type ReactWebComponentAttribute(exportDefault: bool, externalProp: bool) =
    inherit MemberDeclarationPluginAttribute()
    override _.FableMinimumVersion = "3.0"
    
    new() = ReactWebComponentAttribute(exportDefault=false, externalProp = false)
    
    /// <summary>Transforms call-site into createElement calls</summary>
    override _.TransformCall(compiler, memb, expr) =
        let membArgs = memb.CurriedParameterGroups |> List.concat
        match expr with
        | Fable.Call(callee, info, typeInfo, range) when List.length membArgs = List.length info.Args ->
            if info.Args.Length = 1 && AstUtils.isRecord compiler info.Args.[0].Type then
                // F# Component { Value = 1 }
                // JSX <Component Value={1} />
                // JS createElement(Component, { Value: 1 })
                if AstUtils.recordHasField "Key" compiler info.Args.[0].Type then
                    // When the key property is upper-case (which is common in record fields)
                    // then we should rewrite it
                    let modifiedRecord = AstUtils.emitJs "(($value) => { $value.key = $value.Key; return $value; })($0)" [info.Args.[0]]
                    AstUtils.makeCall (AstUtils.makeImport "createElement" "react") [callee; modifiedRecord]
                else
                    AstUtils.makeCall (AstUtils.makeImport "createElement" "react") [callee; info.Args.[0]]
            elif info.Args.Length = 1 && info.Args.[0].Type = Fable.Type.Unit then
                // F# Component()
                // JSX <Component />
                // JS createElement(Component, null)
                AstUtils.makeCall (AstUtils.makeImport "createElement" "react") [callee; AstUtils.nullValue]
            elif info.Args.Length = 1 && externalProp then
                // F# Component()
                // JSX <Component />
                // JS createElement(Component, null)
                AstUtils.makeCall (AstUtils.makeImport "createElement" "react") [callee; info.Args.[0]]
            else
            let propsObj =
                List.zip membArgs info.Args
                |> List.choose (fun (arg, expr) -> arg.Name |> Option.map (fun k -> k, expr))
                |> AstUtils.objExpr
    
            AstUtils.makeCall (AstUtils.makeImport "createElement" "react") [callee; propsObj]
        | _ ->
            // return expression as is when it is not a call expression
            expr
    
    override this.Transform(compiler, file, decl) =
        if decl.Info.IsValue || decl.Info.IsGetter || decl.Info.IsSetter then
            // Invalid attribute usage
            let errorMessage = sprintf "Expecting a function declation for %s when using [<ReactComponent>]" decl.Name
            compiler.LogWarning(errorMessage, ?range=decl.Body.Range)
            decl
        else if not (AstUtils.isReactElement decl.Body.Type) then
            // output of a React function component must be a ReactElement
            let errorMessage = sprintf "Expected function %s to return a ReactElement when using [<ReactComponent>]" decl.Name
            compiler.LogWarning(errorMessage, ?range=decl.Body.Range)
            decl
        else
            if (AstUtils.isCamelCase decl.Name) then
                compiler.LogWarning(sprintf "React function component '%s' is written in camelCase format. Please consider declaring it in PascalCase (i.e. '%s') to follow conventions of React applications and allow tools such as react-refresh to pick it up." decl.Name (AstUtils.capitalize decl.Name))
    
            // do not rewrite components accepting records as input
            if decl.Args.Length = 1 && AstUtils.isRecord compiler decl.Args.[0].Type then
                // check whether the record type is defined in this file
                // trigger warning if that is case
                let definedInThisFile =
                    file.Declarations
                    |> List.tryPick (fun declaration ->
                        match declaration with
                        | Declaration.ClassDeclaration classDecl ->
                            let classEntity = compiler.GetEntity(classDecl.Entity)
                            match decl.Args.[0].Type with
                            | Fable.Type.DeclaredType (entity, genericArgs) ->
                                let declaredEntity = compiler.GetEntity(entity)
                                if classEntity.IsFSharpRecord && declaredEntity.FullName = classEntity.FullName
                                then Some declaredEntity.FullName
                                else None
    
                            | _ -> None
    
                        | Declaration.ActionDeclaration action ->
                            None
                        | _ ->
                            None
                    )
    
                match definedInThisFile with
                | Some recordTypeName ->
                    let errorMsg = String.concat "" [
                        sprintf "Function component '%s' is using a record type '%s' as an input parameter. " decl.Name recordTypeName
                        "This happens to break React tooling like react-refresh and hot module reloading. "
                        "To fix this issue, consider using use an anonymous record instead or use multiple simpler values as input parameters"
                        "Future versions of [<ReactComponent>] might not emit this warning anymore, in which case you can assume that the issue if fixed. "
                        "To learn more about the issue, see https://github.com/pmmmwh/react-refresh-webpack-plugin/issues/258"
                    ]
    
                    compiler.LogWarning(errorMsg, ?range=decl.Body.Range)
    
                | None ->
                    // nothing to report
                    ignore()
    
                { decl with ExportDefault = exportDefault }
            else if decl.Args.Length = 1 && decl.Args.[0].Type = Fable.Type.Unit then
                // remove arguments from functions requiring unit as input
                { decl with Args = [ ]; ExportDefault = exportDefault }
            else
            // rewrite all other arguments into getters of a single props object
            // TODO: transform any callback into into useCallback(callback) to stabilize reference
            let propsArg = AstUtils.makeIdent (sprintf "%sInputProps" (AstUtils.camelCase decl.Name))
            let body =
                ([], decl.Args) ||> List.fold (fun bindings arg ->
                    // TODO: detect usage of "key" and emit warning
                    let getterKind = Fable.ByKey(Fable.ExprKey(AstUtils.makeStrConst arg.DisplayName))
                    let getter = Fable.Get(Fable.IdentExpr propsArg, getterKind, Fable.Any, None)
                    (arg, getter)::bindings)
                |> List.rev
                |> List.fold (fun body (k,v) -> Fable.Let(k, v, body)) decl.Body
    
            { decl with
                Args = [propsArg]
                Body = body
                ExportDefault = exportDefault }


   