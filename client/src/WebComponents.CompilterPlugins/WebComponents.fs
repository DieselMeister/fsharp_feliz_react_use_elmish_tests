﻿namespace WebComponent

open Fable
open Fable.AST
open Fable.AST.Fable
open Utils
    
    
// Tell Fable to scan for plugins in this assembly
[<assembly:ScanForPlugins>]
do()



    
/// <summary>Transforms a function into a React function component. Make sure the function is defined at the module level</summary>
type ReactWebComponentAttribute(exportDefault: bool) =
    inherit MemberDeclarationPluginAttribute()
    override _.FableMinimumVersion = "3.0"
    
    new() = ReactWebComponentAttribute(exportDefault=false)
    
    /// <summary>Transforms call-site into createElement calls</summary>
    override _.TransformCall(compiler, memb, expr) =
        let membArgs = memb.CurriedParameterGroups |> List.concat
        match expr with
        | Fable.Call(callee, info, typeInfo, range) when List.length membArgs = List.length info.Args ->
            // F# Component()
            // JSX <Component />
            // JS createElement(Component, null)



            //Fable.Sequential [
            //    Fable.Let (AstUtils.makeIdent "elem", (AstUtils.makeCall (AstUtils.makeImport "createElement" "react") [callee; info.Args.[0] ]), Fable.Expr.Sequential [])
            //]

            (AstUtils.makeCall (AstUtils.makeImport "createElement" "react") [callee; info.Args.[0] ])
            
        | _ ->
            // return expression as is when it is not a call expression
            compiler.LogWarning("end up in | _ -> ")
            expr
    
    override this.Transform(compiler, file, decl) =
        compiler.LogWarning (decl.Info.ToString())
        match decl with
        | MemberNotFunction ->
            // Invalid attribute usage
            let errorMessage = sprintf "Expecting a function declation for %s when using [<ReactWebComponent>]" decl.Name
            compiler.LogWarning(errorMessage, ?range=decl.Body.Range)
            decl
        | MemberNotReturningReactElement ->
            // output of a React function component must be a ReactElement
            let errorMessage = sprintf "Expected function %s to return a ReactElement when using [<ReactWebComponent>]" decl.Name
            compiler.LogWarning(errorMessage, ?range=decl.Body.Range)
            decl
        | _ ->
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
                compiler.LogError "ReactWebComponents only accept one anonymous record or unit as parameter."
                decl



type CreateReactWebComponentAttribute(customElementName:string) =
    inherit MemberDeclarationPluginAttribute()
    override _.FableMinimumVersion = "3.0"

    override _.TransformCall(compiler, memb, expr) =
        expr
    
    override this.Transform(compiler, file, decl) =
        match decl.Body with
        | Fable.Lambda(arg, body, name) ->
            match arg.Type with
            | Fable.AnonymousRecordType(fieldName,typList) ->
                let allAreTypesStrings = typList |> List.forall (fun t -> t = Fable.String)
                if (not allAreTypesStrings) then
                    compiler.LogError "For Webcomponents all properties of the anonymous record must be from type string"
                    decl
                else
                    let oldBody = decl.Body
                    let propTypesRequiredStr =
                        System.String.Join(
                            ", ",
                            fieldName 
                            |> Array.map (fun e -> sprintf "%s: PropTypes.string.isRequired" e)
                        )
                        
                        
                    let isShadowDom = true

                    let webCompBody =
                        Fable.Sequential [
                
                            let reactFunctionWithPropsBody = 
                                AstUtils.makeCall
                                    (AstUtils.makeAnonFunction
                                        AstUtils.unitIdent
                                        (Fable.Sequential [
                                            AstUtils.emitJs "const elem = $0" [ oldBody ]
                                            AstUtils.makeImport "PropTypes" "prop-types"
                                            AstUtils.emitJs (sprintf "elem.propTypes = { %s }" propTypesRequiredStr) []
                                            AstUtils.emitJs "elem" []
                                        ])
                                    )
                                    []


                            let webComCall =
                                AstUtils.makeCall 
                                    (AstUtils.makeImport "default" "react-to-webcomponent") 
                                    [ 
                                        reactFunctionWithPropsBody; 
                                        AstUtils.makeImport "default" "react"
                                        AstUtils.makeImport "default" "react-dom"
                                        AstUtils.emitJs "{ shadow: true }" []
                                    ]
                
                
                            AstUtils.emitJs "customElements.define($0,$1)" [ AstUtils.makeStrConst customElementName ; webComCall ]
                        ]
                
                    let decl = {
                        decl with
                            Body = webCompBody
                    }
        
                    decl
            | _ ->
                compiler.LogError "the react function is not declared with an anonymous record as paramater!"    
                decl
        | _ ->
            compiler.LogError "The imput for the web component must be a react element function generated from [<ReactWebComponents>]!"
            decl



            //if (AstUtils.isCamelCase decl.Name) then
            //    compiler.LogWarning(sprintf "React function component '%s' is written in camelCase format. Please consider declaring it in PascalCase (i.e. '%s') to follow conventions of React applications and allow tools such as react-refresh to pick it up." decl.Name (AstUtils.capitalize decl.Name))
    
            //// do not rewrite components accepting records as input
            //if decl.Args.Length = 1 && AstUtils.isRecord compiler decl.Args.[0].Type then
            //    // check whether the record type is defined in this file
            //    // trigger warning if that is case
            //    let definedInThisFile =
            //        file.Declarations
            //        |> List.tryPick (fun declaration ->
            //            match declaration with
            //            | Declaration.ClassDeclaration classDecl ->
            //                let classEntity = compiler.GetEntity(classDecl.Entity)
            //                match decl.Args.[0].Type with
            //                | Fable.Type.DeclaredType (entity, genericArgs) ->
            //                    let declaredEntity = compiler.GetEntity(entity)
            //                    if classEntity.IsFSharpRecord && declaredEntity.FullName = classEntity.FullName
            //                    then Some declaredEntity.FullName
            //                    else None
    
            //                | _ -> None
    
            //            | Declaration.ActionDeclaration action ->
            //                None
            //            | _ ->
            //                None
            //        )
    
            //    match definedInThisFile with
            //    | Some recordTypeName ->
            //        let errorMsg = String.concat "" [
            //            sprintf "Function component '%s' is using a record type '%s' as an input parameter. " decl.Name recordTypeName
            //            "This happens to break React tooling like react-refresh and hot module reloading. "
            //            "To fix this issue, consider using use an anonymous record instead or use multiple simpler values as input parameters"
            //            "Future versions of [<ReactComponent>] might not emit this warning anymore, in which case you can assume that the issue if fixed. "
            //            "To learn more about the issue, see https://github.com/pmmmwh/react-refresh-webpack-plugin/issues/258"
            //        ]
    
            //        compiler.LogWarning(errorMsg, ?range=decl.Body.Range)
    
            //    | None ->
            //        // nothing to report
            //        ignore()
    
            //    { decl with ExportDefault = exportDefault }
            //else if decl.Args.Length = 1 && decl.Args.[0].Type = Fable.Type.Unit then
            //    // remove arguments from functions requiring unit as input
            //    { decl with Args = [ ]; ExportDefault = exportDefault }
            //else
            //    compiler.LogError "ReactWebComponents only accept one anonymous record or unit as parameter."
            //    decl
            

        //if decl.Info.IsValue || decl.Info.IsGetter || decl.Info.IsSetter then
            
        //else if not (AstUtils.isReactElement decl.Body.Type) then
            
        //else
            


   