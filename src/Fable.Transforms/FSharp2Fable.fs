module rec Fable.Transforms.FSharp2Fable.Compiler

open System.Collections.Generic
open FSharp.Compiler.SyntaxTree
open FSharp.Compiler.SourceCodeServices

open Fable
open Fable.AST
open Fable.Transforms

open MonadicTrampoline
open Patterns
open TypeHelpers
open Identifiers
open Helpers
open Util

let inline private transformExprList com ctx xs = trampolineListMap (transformExpr com ctx) xs
let inline private transformExprOpt com ctx opt = trampolineOptionMap (transformExpr com ctx) opt

// Fable doesn't support arguments passed by ref, see #1696
let private checkArgumentsPassedByRef com ctx (args: FSharpExpr list) =
    for arg in args do
        match arg with
        | BasicPatterns.AddressOf _ ->
            "Arguments cannot be passed byref"
            |> addWarning com ctx.InlinePath (makeRangeFrom arg)
        | _ -> ()

let private transformBaseConsCall com ctx r baseEnt (baseCons: FSharpMemberOrFunctionOrValue) genArgs baseArgs =
    let baseArgs = transformExprList com ctx baseArgs |> run
    let genArgs = genArgs |> Seq.map (makeType com ctx.GenericArgs)
    match Replacements.tryBaseConstructor com baseEnt baseCons genArgs baseArgs with
    | Some(baseRef, args) ->
        let callInfo: Fable.CallInfo =
          { ThisArg = None
            Args = args
            SignatureArgTypes = getArgTypes com baseCons
            HasSpread = false
            AutoUncurrying = false
            IsJsConstructor = false }
        makeCall r Fable.Unit callInfo baseRef
    | None ->
        if not(isImplicitConstructor com baseEnt baseCons) then
            "Only inheriting from primary constructors is supported"
            |> addErrorAndReturnNull com [] r
        else
            match makeCallFrom com ctx r Fable.Unit genArgs None baseArgs baseCons with
            | Fable.Operation(Fable.Call(_, info), t, r) ->
                let baseExpr = entityRef com baseEnt
                Fable.Operation(Fable.Call(baseExpr, info), t, r)
            | e -> e // Unexpected, throw error?

let private transformNewUnion com ctx r fsType (unionCase: FSharpUnionCase) (argExprs: Fable.Expr list) =
    match fsType with
    | ErasedUnion(tdef, genArgs, rule) ->
        match argExprs with
        | [] -> transformStringEnum rule unionCase
        | [argExpr] -> argExpr
        | _ when tdef.UnionCases.Count > 1 ->
            "Erased unions with multiple cases must have one single field: " + (getFsTypeFullName fsType)
            |> addErrorAndReturnNull com ctx.InlinePath r
        | argExprs -> Fable.NewTuple argExprs |> makeValue r
    | StringEnum(tdef, rule) ->
        match argExprs with
        | [] -> transformStringEnum rule unionCase
        | _ -> sprintf "StringEnum types cannot have fields: %O" tdef.TryFullName
               |> addErrorAndReturnNull com ctx.InlinePath r
    | OptionUnion typ ->
        let typ = makeType com ctx.GenericArgs typ
        let expr =
            match argExprs with
            | [] -> None
            | [expr] -> Some expr
            | _ -> failwith "Unexpected args for Option constructor"
        Fable.NewOption(expr, typ) |> makeValue r
    | ListUnion typ ->
        let typ = makeType com ctx.GenericArgs typ
        let headAndTail =
            match argExprs with
            | [] -> None
            | [head; tail] -> Some(head, tail)
            | _ -> failwith "Unexpected args for List constructor"
        Fable.NewList(headAndTail, typ) |> makeValue r
    | DiscriminatedUnion(tdef, genArgs) ->
        let genArgs = makeGenArgs com ctx.GenericArgs genArgs
        Fable.NewUnion(argExprs, unionCase, tdef, genArgs) |> makeValue r

let private transformTraitCall com (ctx: Context) r typ (sourceTypes: FSharpType list) traitName (flags: MemberFlags) (argTypes: FSharpType list) (argExprs: FSharpExpr list) =
    let makeCallInfo traitName entityFullName argTypes genArgs: Fable.ReplaceCallInfo =
        { SignatureArgTypes = argTypes
          DeclaringEntityFullName = entityFullName
          HasSpread = false
          IsModuleValue = false
          // We only need this for types with own entries in Fable AST
          // (no interfaces, see below) so it's safe to set this to false
          IsInterface = false
          CompiledName = traitName
          OverloadSuffix = lazy ""
          GenericArgs =
            // TODO: Check the source F# entity to get the actual gen param names?
            match genArgs with
            | [] -> []
            | [genArg] -> ["T", genArg]
            | genArgs -> genArgs |> List.mapi (fun i genArg -> "T" + string i, genArg)
        }

    let resolveMemberCall (entity: FSharpEntity) genArgs membCompiledName isInstance argTypes thisArg args =
        let genArgs = matchGenericParams genArgs entity.GenericParameters
        tryFindMember com entity (Map genArgs) membCompiledName isInstance argTypes
        |> Option.map (fun memb -> makeCallFrom com ctx r typ [] thisArg args memb)

    let isInstance = flags.IsInstance
    let argTypes = List.map (makeType com ctx.GenericArgs) argTypes
    let argExprs = List.map (fun e -> com.Transform(ctx, e)) argExprs
    let thisArg, args, argTypes =
        match argExprs, argTypes with
        | thisArg::args, _::argTypes when isInstance -> Some thisArg, args, argTypes
        | args, argTypes -> None, args, argTypes

    sourceTypes |> Seq.tryPick (fun sourceType ->
        let t = makeType com ctx.GenericArgs sourceType
        match t with
        // Types with specific entry in Fable.AST
        // TODO: Check other types like booleans or numbers?
        | Fable.String ->
            let info = makeCallInfo traitName Types.string argTypes []
            Replacements.strings com ctx r typ info thisArg args
        | Fable.Tuple genArgs ->
            let info = makeCallInfo traitName (getTypeFullName false t) argTypes genArgs
            Replacements.tuples com ctx r typ info thisArg args
        | Fable.Option genArg ->
            let info = makeCallInfo traitName Types.option argTypes [genArg]
            Replacements.options com ctx r typ info thisArg args
        | Fable.Array genArg ->
            let info = makeCallInfo traitName Types.array argTypes [genArg]
            Replacements.arrays com ctx r typ info thisArg args
        | Fable.List genArg ->
            let info = makeCallInfo traitName Types.list argTypes [genArg]
            Replacements.lists com ctx r typ info thisArg args
        // Declared types not in Fable AST
        | Fable.DeclaredType(entity, genArgs) ->
            // SRTP only works for records if there are no arguments
            if isInstance && entity.IsFSharpRecord && List.isEmpty args && Option.isSome thisArg then
                let fieldName = Naming.removeGetSetPrefix traitName
                entity.FSharpFields |> Seq.tryPick (fun fi ->
                    if fi.Name = fieldName then
                        let kind = Fable.FieldGet(fi.Name, fi.IsMutable, makeType com Map.empty fi.FieldType)
                        Fable.Get(thisArg.Value, kind, typ, r) |> Some
                    else None)
                |> Option.orElseWith (fun () ->
                    resolveMemberCall entity genArgs traitName isInstance argTypes thisArg args)
            else resolveMemberCall entity genArgs traitName isInstance argTypes thisArg args
        | Fable.AnonymousRecordType(sortedFieldNames, genArgs)
                when isInstance && List.isEmpty args && Option.isSome thisArg ->
            let fieldName = Naming.removeGetSetPrefix traitName
            Seq.zip sortedFieldNames genArgs
            |> Seq.tryPick (fun (fi, fiType) ->
                if fi = fieldName then
                    let kind = Fable.FieldGet(fi, false, fiType)
                    Fable.Get(thisArg.Value, kind, typ, r) |> Some
                else None)
        | _ -> None
    ) |> Option.defaultWith (fun () ->
        "Cannot resolve trait call " + traitName |> addErrorAndReturnNull com ctx.InlinePath r)

let private getAttachedMemberInfo com ctx r nonMangledNameConflicts
                (declaringEntity: FSharpEntity option) (sign: FSharpAbstractSignature): Fable.AttachedMemberInfo =
    let entityName =
        match declaringEntity with
        | Some e -> getEntityDeclarationName com e
        | None -> ""
    let isGetter = sign.Name.StartsWith("get_")
    let isSetter = not isGetter && sign.Name.StartsWith("set_")
    let indexedProp = (isGetter && countNonCurriedParamsForSignature sign > 0)
                        || (isSetter && countNonCurriedParamsForSignature sign > 1)
    let name, isGetter, isSetter, isEnumerator, hasSpread =
        // Don't use the type from the arguments as the override may come
        // from another type, like ToString()
        match tryDefinition sign.DeclaringType with
        | Some(ent, fullName) ->
            let isEnumerator =
                sign.Name = "GetEnumerator"
                && fullName = Some "System.Collections.Generic.IEnumerable`1"
            let hasSpread =
                if isGetter || isSetter then false
                else
                    // FSharpObjectExprOverride.CurriedParameterGroups doesn't offer
                    // information about ParamArray, we need to check the source method.
                    ent.TryGetMembersFunctionsAndValues
                    |> Seq.tryFind (fun x -> x.CompiledName = sign.Name)
                    |> function Some m -> hasSeqSpread m | None -> false
            let name, isGetter, isSetter =
                if isMangledAbstractEntity ent then
                    let overloadHash =
                        if (isGetter || isSetter) && not indexedProp then ""
                        else OverloadSuffix.getAbstractSignatureHash ent sign
                    getMangledAbstractMemberName ent sign.Name overloadHash, false, false
                else
                    let name, isGetter, isSetter =
                        // For indexed properties, keep the get_/set_ prefix and compile as method
                        if indexedProp then sign.Name, false, false
                        else Naming.removeGetSetPrefix sign.Name, isGetter, isSetter
                    // Setters can have same name as getters, assume there will always be a getter
                    if not isSetter && nonMangledNameConflicts entityName name then
                        sprintf "Member %s is duplicated, use Mangle attribute to prevent conflicts with interfaces" name
                        |> addError com ctx.InlinePath r
                    name, isGetter, isSetter
            name, isGetter, isSetter, isEnumerator, hasSpread
        | None ->
            Naming.removeGetSetPrefix sign.Name, isGetter, isSetter, false, false
    Fable.AttachedMemberInfo(name, declaringEntity, hasSpread, false, isGetter, isSetter, isEnumerator, ?range=r)

let private transformObjExpr (com: IFableCompiler) (ctx: Context) (objType: FSharpType)
                    baseCallExpr (overrides: FSharpObjectExprOverride list) otherOverrides =

    let nonMangledMemberNames = HashSet()
    let nonMangledNameConflicts _ name =
        nonMangledMemberNames.Add(name) |> not

    let mapOverride (over: FSharpObjectExprOverride) =
      trampoline {
        let ctx, args = bindMemberArgs com ctx over.CurriedParameterGroups
        let! body = transformExpr com ctx over.Body
        let info = getAttachedMemberInfo com ctx body.Range nonMangledNameConflicts None over.Signature
        return args, body, info
      }

    trampoline {
      let! baseCall =
        trampoline {
            match baseCallExpr with
            // TODO: For interface implementations this should be BasicPatterns.NewObject
            // but check the baseCall.DeclaringEntity name just in case
            | BasicPatterns.Call(None,baseCall,genArgs1,genArgs2,baseArgs) ->
                match baseCall.DeclaringEntity with
                | Some baseType when baseType.TryFullName <> Some Types.object ->
                    let typ = makeType com ctx.GenericArgs baseCallExpr.Type
                    let! baseArgs = transformExprList com ctx baseArgs
                    let genArgs = genArgs1 @ genArgs2 |> Seq.map (makeType com ctx.GenericArgs)
                    return makeCallFrom com ctx None typ genArgs None baseArgs baseCall |> Some
                | _ -> return None
            | _ -> return None
        }

      let! members =
        (objType, overrides)::otherOverrides
        |> trampolineListMap (fun (_typ, overrides) ->
            overrides |> trampolineListMap mapOverride)

      return Fable.ObjectExpr(members |> List.concat, makeType com ctx.GenericArgs objType, baseCall)
    }

let private transformDelegate com ctx delegateType expr =
  trampoline {
    let! expr = transformExpr com ctx expr
    match makeType com ctx.GenericArgs delegateType with
    | Fable.FunctionType(Fable.DelegateType argTypes, _) ->
        let arity = List.length argTypes |> max 1
        match expr with
        | LambdaUncurriedAtCompileTime (Some arity) lambda -> return lambda
        | _ -> return Replacements.uncurryExprAtRuntime arity expr
    | _ -> return expr
  }

let private transformUnionCaseTest (com: IFableCompiler) (ctx: Context) r
                            unionExpr fsType (unionCase: FSharpUnionCase) =
  trampoline {
    let! unionExpr = transformExpr com ctx unionExpr
    match fsType with
    | ErasedUnion(tdef, genArgs, rule) ->
        match unionCase.UnionCaseFields.Count with
        | 0 -> return makeEqOp r unionExpr (transformStringEnum rule unionCase) BinaryEqualStrict
        | 1 ->
            let fi = unionCase.UnionCaseFields.[0]
            let typ =
                if fi.FieldType.IsGenericParameter then
                    let name = genParamName fi.FieldType.GenericParameter
                    let index =
                        tdef.GenericParameters
                        |> Seq.findIndex (fun arg -> arg.Name = name)
                    genArgs.[index]
                else fi.FieldType
            let kind = makeType com ctx.GenericArgs typ |> Fable.TypeTest
            return Fable.Test(unionExpr, kind, r)
        | _ ->
            return "Erased unions with multiple cases cannot have more than one field: " + (getFsTypeFullName fsType)
            |> addErrorAndReturnNull com ctx.InlinePath r
    | OptionUnion _ ->
        let kind = Fable.OptionTest(unionCase.Name <> "None" && unionCase.Name <> "ValueNone")
        return Fable.Test(unionExpr, kind, r)
    | ListUnion _ ->
        let kind = Fable.ListTest(unionCase.CompiledName <> "Empty")
        return Fable.Test(unionExpr, kind, r)
    | StringEnum(_, rule) ->
        return makeEqOp r unionExpr (transformStringEnum rule unionCase) BinaryEqualStrict
    | DiscriminatedUnion(tdef,_) ->
        let kind = Fable.UnionCaseTest(unionCase, tdef)
        return Fable.Test(unionExpr, kind, r)
  }

let rec private transformDecisionTargets (com: IFableCompiler) (ctx: Context) acc
                    (xs: (FSharpMemberOrFunctionOrValue list * FSharpExpr) list) =
    trampoline {
        match xs with
        | [] -> return List.rev acc
        | (idents, expr)::tail ->
            let ctx, idents =
                (idents, (ctx, [])) ||> List.foldBack (fun ident (ctx, idents) ->
                    let ctx, ident = putArgInScope com ctx ident
                    ctx, ident::idents)
            let! expr = transformExpr com ctx expr
            return! transformDecisionTargets com ctx ((idents, expr)::acc) tail
    }

let private transformExpr (com: IFableCompiler) (ctx: Context) fsExpr =
  trampoline {
    match fsExpr with
    | OptimizedOperator(memb, comp, opName, argTypes, argExprs) ->
        let r, typ = makeRangeFrom fsExpr, makeType com ctx.GenericArgs fsExpr.Type
        let argTypes = argTypes |> List.map (makeType com ctx.GenericArgs)
        let! args = transformExprList com ctx argExprs
        let entity =
            match comp with
            | Some comp -> comp.DeclaringEntity.Value
            | None -> memb.DeclaringEntity.Value
        let membOpt = tryFindMember com entity ctx.GenericArgs opName false argTypes
        return (match membOpt with
                | Some memb -> makeCallFrom com ctx r typ argTypes None args memb
                | None -> failwithf "Cannot find member %A.%A" (entity.FullName) opName)

    | BasicPatterns.Coerce(targetType, inpExpr) ->
        let! (inpExpr: Fable.Expr) = transformExpr com ctx inpExpr
        let t = makeType com ctx.GenericArgs targetType
        match tryDefinition targetType with
        | Some(_, Some fullName) ->
            match fullName with
            | Types.ienumerableGeneric | Types.ienumerable -> return Replacements.toSeq t inpExpr
            | _ -> return Fable.TypeCast(inpExpr, t)
        | _ -> return Fable.TypeCast(inpExpr, t)

    // TypeLambda is a local generic lambda
    // e.g, member x.Test() = let typeLambda x = x in typeLambda 1, typeLambda "A"
    // Sometimes these must be inlined, but that's resolved in BasicPatterns.Let (see below)
    | BasicPatterns.TypeLambda (_genArgs, lambda) ->
        let! lambda = transformExpr com ctx lambda
        return lambda

    | ByrefArgToTuple (callee, memb, ownerGenArgs, membGenArgs, membArgs) ->
        let! callee = transformExprOpt com ctx callee
        let! args = transformExprList com ctx membArgs
        let genArgs = ownerGenArgs @ membGenArgs |> Seq.map (makeType com ctx.GenericArgs)
        let typ = makeType com ctx.GenericArgs fsExpr.Type
        return makeCallFrom com ctx (makeRangeFrom fsExpr) typ genArgs callee args memb

    | ByrefArgToTupleOptimizedIf (outArg, callee, memb, ownerGenArgs, membGenArgs, membArgs, thenExpr, elseExpr) ->
        let ctx, ident = putArgInScope com ctx outArg
        let! callee = transformExprOpt com ctx callee
        let! args = transformExprList com ctx membArgs
        let genArgs = ownerGenArgs @ membGenArgs |> Seq.map (makeType com ctx.GenericArgs)
        let byrefType = makeType com ctx.GenericArgs (List.last membArgs).Type
        let tupleType = [Fable.Boolean; byrefType] |> Fable.Tuple
        let tupleIdent = getIdentUniqueName ctx "tuple" |> makeIdent
        let tupleIdentExpr = Fable.IdentExpr tupleIdent
        let tupleExpr = makeCallFrom com ctx None tupleType genArgs callee args memb
        let identExpr = Fable.Get(tupleIdentExpr, Fable.TupleGet 1, tupleType, None)
        let guardExpr = Fable.Get(tupleIdentExpr, Fable.TupleGet 0, tupleType, None)
        let! thenExpr = transformExpr com ctx thenExpr
        let! elseExpr = transformExpr com ctx elseExpr
        let ifThenElse = Fable.IfThenElse(guardExpr, thenExpr, elseExpr, None)
        return Fable.Let([tupleIdent, tupleExpr], Fable.Let([ident, identExpr], ifThenElse))

    | ByrefArgToTupleOptimizedTree (outArg, callee, memb, ownerGenArgs, membGenArgs, membArgs, thenExpr, elseExpr, targetsExpr) ->
        let ctx, ident = putArgInScope com ctx outArg
        let! callee = transformExprOpt com ctx callee
        let! args = transformExprList com ctx membArgs
        let genArgs = ownerGenArgs @ membGenArgs |> Seq.map (makeType com ctx.GenericArgs)
        let byrefType = makeType com ctx.GenericArgs (List.last membArgs).Type
        let tupleType = [Fable.Boolean; byrefType] |> Fable.Tuple
        let tupleIdentExpr = Fable.IdentExpr ident
        let tupleExpr = makeCallFrom com ctx None tupleType genArgs callee args memb
        let guardExpr = Fable.Get(tupleIdentExpr, Fable.TupleGet 0, tupleType, None)
        let! thenExpr = transformExpr com ctx thenExpr
        let! elseExpr = transformExpr com ctx elseExpr
        let! targetsExpr = transformDecisionTargets com ctx [] targetsExpr
        let ifThenElse = Fable.IfThenElse(guardExpr, thenExpr, elseExpr, None)
        return Fable.Let([ident, tupleExpr], Fable.DecisionTree(ifThenElse, targetsExpr))

    | ByrefArgToTupleOptimizedLet (id1, id2, callee, memb, ownerGenArgs, membGenArgs, membArgs, restExpr) ->
        let ctx, ident1 = putArgInScope com ctx id1
        let ctx, ident2 = putArgInScope com ctx id2
        let! callee = transformExprOpt com ctx callee
        let! args = transformExprList com ctx membArgs
        let genArgs = ownerGenArgs @ membGenArgs |> Seq.map (makeType com ctx.GenericArgs)
        let byrefType = makeType com ctx.GenericArgs (List.last membArgs).Type
        let tupleType = [Fable.Boolean; byrefType] |> Fable.Tuple
        let tupleIdent = getIdentUniqueName ctx "tuple" |> makeIdent
        let tupleIdentExpr = Fable.IdentExpr tupleIdent
        let tupleExpr = makeCallFrom com ctx None tupleType genArgs callee args memb
        let id1Expr = Fable.Get(tupleIdentExpr, Fable.TupleGet 0, tupleType, None)
        let id2Expr = Fable.Get(tupleIdentExpr, Fable.TupleGet 1, tupleType, None)
        let! restExpr = transformExpr com ctx restExpr
        let body = Fable.Let([ident1, id1Expr], Fable.Let([ident2, id2Expr], restExpr))
        return Fable.Let([tupleIdent, tupleExpr], body)

    | CallCreateEvent (callee, eventName, memb, ownerGenArgs, membGenArgs, membArgs) ->
        let! callee = transformExpr com ctx callee
        let! args = transformExprList com ctx membArgs
        let callee = get None Fable.Any callee eventName
        let genArgs = ownerGenArgs @ membGenArgs |> Seq.map (makeType com ctx.GenericArgs)
        let typ = makeType com ctx.GenericArgs fsExpr.Type
        return makeCallFrom com ctx (makeRangeFrom fsExpr) typ genArgs (Some callee) args memb

    | BindCreateEvent (var, value, eventName, body) ->
        let! value = transformExpr com ctx value
        let value = get None Fable.Any value eventName
        let ctx, ident = putBindingInScope com ctx var value
        let! body = transformExpr com ctx body
        return Fable.Let([ident, value], body)

    // TODO: Detect if it's ResizeArray and compile as FastIntegerForLoop?
    | ForOf (PutArgInScope com ctx (newContext, ident), value, body) ->
        let! value = transformExpr com ctx value
        let! body = transformExpr com newContext body
        return Replacements.iterate (makeRangeFrom fsExpr) ident body value

    // Flow control
    | BasicPatterns.FastIntegerForLoop(start, limit, body, isUp) ->
        let r = makeRangeFrom fsExpr
        match body with
        | BasicPatterns.Lambda (PutArgInScope com ctx (newContext, ident), body) ->
            let! start = transformExpr com ctx start
            let! limit = transformExpr com ctx limit
            let! body = transformExpr com newContext body
            return Fable.For (ident, start, limit, body, isUp)
            |> makeLoop r
        | _ -> return failwithf "Unexpected loop %O: %A" r fsExpr

    | BasicPatterns.WhileLoop(guardExpr, bodyExpr) ->
        let! guardExpr = transformExpr com ctx guardExpr
        let! bodyExpr = transformExpr com ctx bodyExpr
        return Fable.While (guardExpr, bodyExpr)
        |> makeLoop (makeRangeFrom fsExpr)

    // Values
    | BasicPatterns.Const(value, typ) ->
        let typ = makeType com ctx.GenericArgs typ
        return Replacements.makeTypeConst (makeRangeFrom fsExpr) typ value

    | BasicPatterns.BaseValue typ ->
        let r = makeRangeFrom fsExpr
        let typ = makeType com Map.empty typ
        return Fable.Value(Fable.BaseValue(ctx.BoundMemberThis, typ), r)

    // F# compiler doesn't represent `this` in non-constructors as BasicPatterns.ThisValue (but BasicPatterns.Value)
    | BasicPatterns.ThisValue typ ->
        let r = makeRangeFrom fsExpr
        return
            match typ, ctx.BoundConstructorThis with
            // When it's ref type, this is the x in `type C() as x =`
            | RefType _, _ ->
                tryGetIdentFromScopeIf ctx r (fun fsRef -> fsRef.IsConstructorThisValue)
                |> Option.defaultWith (fun () -> "Cannot find ConstructorThisValue"
                                                 |> addErrorAndReturnNull com ctx.InlinePath r)
            // Check if `this` has been bound previously to avoid conflicts with an object expression
            | _, Some i -> identWithRange r i |> Fable.IdentExpr
            | _, None -> Fable.Value(makeType com Map.empty typ |> Fable.ThisValue, r)

    | BasicPatterns.Value var ->
        let r = makeRangeFrom fsExpr
        if isInline var then
            match ctx.ScopeInlineValues |> List.tryFind (fun (v,_) -> obj.Equals(v, var)) with
            | Some (_,fsExpr) ->
                return! transformExpr com ctx fsExpr
            | None ->
                return "Cannot resolve locally inlined value: " + var.DisplayName
                |> addErrorAndReturnNull com ctx.InlinePath r
        else
            return makeValueFrom com ctx r var

    | BasicPatterns.DefaultValue (FableType com ctx typ) ->
        return Replacements.defaultof com ctx typ

    // Capture variable generic type mapping
    | BasicPatterns.Let((var, value), (BasicPatterns.Application(_body, genArgs, _args) as expr)) ->
        let genArgs = Seq.map (makeType com ctx.GenericArgs) genArgs
        let ctx = { ctx with GenericArgs = matchGenericParamsFrom var genArgs |> Map }
        let! value = transformExpr com ctx value
        let ctx, ident = putBindingInScope com ctx var value
        let! body = transformExpr com ctx expr
        return Fable.Let([ident, value], body)

    // Assignments
    | BasicPatterns.Let((var, value), body) ->
        if isInline var then
            let ctx = { ctx with ScopeInlineValues = (var, value)::ctx.ScopeInlineValues }
            return! transformExpr com ctx body
        else
            let! value = transformExpr com ctx value
            let ctx, ident = putBindingInScope com ctx var value
            let! body = transformExpr com ctx body
            return Fable.Let([ident, value], body)

    | BasicPatterns.LetRec(recBindings, body) ->
        // First get a context containing all idents and use it compile the values
        let ctx, idents =
            (recBindings, (ctx, []))
            ||> List.foldBack (fun (PutArgInScope com ctx (newContext, ident), _) (ctx, idents) ->
                (newContext, ident::idents))
        let _, bindingExprs = List.unzip recBindings
        let! exprs = transformExprList com ctx bindingExprs
        let bindings = List.zip idents exprs
        let! body = transformExpr com ctx body
        return Fable.Let(bindings, body)

    // Applications
    | CapturedBaseConsCall com ctx transformBaseConsCall nextExpr ->
        return! transformExpr com ctx nextExpr

    // `argTypes2` is always empty
    | BasicPatterns.TraitCall(sourceTypes, traitName, flags, argTypes, _argTypes2, argExprs) ->
        let typ = makeType com ctx.GenericArgs fsExpr.Type
        return transformTraitCall com ctx (makeRangeFrom fsExpr) typ sourceTypes traitName flags argTypes argExprs

    | BasicPatterns.Call(callee, memb, ownerGenArgs, membGenArgs, args) ->
        checkArgumentsPassedByRef com ctx args
        let! callee = transformExprOpt com ctx callee
        let! args = transformExprList com ctx args
        // TODO: Check answer to #868 in FSC repo
        let genArgs = ownerGenArgs @ membGenArgs |> Seq.map (makeType com ctx.GenericArgs)
        let typ = makeType com ctx.GenericArgs fsExpr.Type
        return makeCallFrom com ctx (makeRangeFrom fsExpr) typ genArgs callee args memb

    | BasicPatterns.Application(applied, _genArgs, []) ->
        // TODO: Ask why application without arguments happen. So far I've seen it
        // to access None or struct values (like the Result type)
        return! transformExpr com ctx applied

    // Application of locally inlined lambdas
    | BasicPatterns.Application(BasicPatterns.Value var, genArgs, args) when isInline var ->
        let r = makeRangeFrom fsExpr
        match ctx.ScopeInlineValues |> List.tryFind (fun (v,_) -> obj.Equals(v, var)) with
        | Some (_,fsExpr) ->
            let genArgs = Seq.map (makeType com ctx.GenericArgs) genArgs
            let resolvedCtx = { ctx with GenericArgs = matchGenericParamsFrom var genArgs |> Map }
            let! callee = transformExpr com resolvedCtx fsExpr
            match args with
            | [] -> return callee
            | args ->
                let typ = makeType com ctx.GenericArgs fsExpr.Type
                let! args = transformExprList com ctx args
                return Fable.Operation(Fable.CurriedApply(callee, args), typ, r)
        | None ->
            return "Cannot resolve locally inlined value: " + var.DisplayName
            |> addErrorAndReturnNull com ctx.InlinePath r

    // When using Fable dynamic operator, we must untuple arguments
    // Note F# compiler wraps the value in a closure if it detects it's a lambda
    | BasicPatterns.Application(BasicPatterns.Let((_, BasicPatterns.Call(None,m,_,_,[e1; e2])),_), _genArgs, args)
            when m.FullName = "Fable.Core.JsInterop.( ? )" ->
        let! e1 = transformExpr com ctx e1
        let! e2 = transformExpr com ctx e2
        let e = Fable.Get(e1, Fable.ExprGet e2, Fable.Any, e1.Range)
        let! args = transformExprList com ctx args
        let args = destructureTupleArgs args
        let typ = makeType com ctx.GenericArgs fsExpr.Type
        let callInfo = { makeSimpleCallInfo None args [] with AutoUncurrying = true }
        return Fable.Operation(Fable.Call(e, callInfo), typ, makeRangeFrom fsExpr)

    // Some instance members such as Option.get_IsSome are compiled as static members, and the F# compiler
    // wraps calls with an application. But in Fable they will be replaced so the application is not needed
    | BasicPatterns.Application(BasicPatterns.Call(Some _, memb, _, [], []) as call, _genArgs, [BasicPatterns.Const(null, _)])
         when memb.IsInstanceMember && not memb.IsInstanceMemberInCompiledCode ->
         return! transformExpr com ctx call

    | BasicPatterns.Application(applied, _genArgs, args) ->
        let! applied = transformExpr com ctx applied
        let! args = transformExprList com ctx args
        let typ = makeType com ctx.GenericArgs fsExpr.Type
        return Fable.Operation(Fable.CurriedApply(applied, args), typ, makeRangeFrom fsExpr)

    | BasicPatterns.IfThenElse (guardExpr, thenExpr, elseExpr) ->
        let! guardExpr = transformExpr com ctx guardExpr
        let! thenExpr = transformExpr com ctx thenExpr
        let! fableElseExpr = transformExpr com ctx elseExpr

        let altElseExpr =
            match elseExpr with
            | RaisingMatchFailureExpr _fileNameWhereErrorOccurs ->
                let errorMessage = "The match cases were incomplete"
                let rangeOfElseExpr = makeRangeFrom elseExpr
                let errorExpr = Replacements.Helpers.error (Fable.Value(Fable.StringConstant errorMessage, None))
                Fable.Throw(errorExpr, Fable.Any, rangeOfElseExpr)
            | _ ->
                fableElseExpr

        return Fable.IfThenElse(guardExpr, thenExpr, altElseExpr, makeRangeFrom fsExpr)

    | BasicPatterns.TryFinally (body, finalBody) ->
        let r = makeRangeFrom fsExpr
        match body with
        | BasicPatterns.TryWith(body, _, _, catchVar, catchBody) ->
            return makeTryCatch com ctx r body (Some (catchVar, catchBody)) (Some finalBody)
        | _ -> return makeTryCatch com ctx r body None (Some finalBody)

    | BasicPatterns.TryWith (body, _, _, catchVar, catchBody) ->
        return makeTryCatch com ctx (makeRangeFrom fsExpr) body (Some (catchVar, catchBody)) None

    // Lambdas
    | BasicPatterns.NewDelegate(delegateType, fsExpr) ->
        return! transformDelegate com ctx delegateType fsExpr

    | BasicPatterns.Lambda(arg, body) ->
        let ctx, args = makeFunctionArgs com ctx [arg]
        match args with
        | [arg] ->
            let! body = transformExpr com ctx body
            return Fable.Function(Fable.Lambda arg, body, None)
        | _ -> return failwith "makeFunctionArgs returns args with different length"

    // Getters and Setters
    | BasicPatterns.AnonRecordGet(callee, calleeType, fieldIndex) ->
        let! callee = transformExpr com ctx callee
        let fieldName = calleeType.AnonRecordTypeDetails.SortedFieldNames.[fieldIndex]
        let typ = makeType com ctx.GenericArgs fsExpr.Type
        let kind = Fable.FieldGet(fieldName, false, typ)
        return Fable.Get(callee, kind, typ, makeRangeFrom fsExpr)

    | BasicPatterns.FSharpFieldGet(callee, calleeType, field) ->
        let! callee = transformExprOpt com ctx callee
        let callee =
            match callee with
            | Some callee -> callee
            | None -> entityRef com calleeType.TypeDefinition
        let kind = Fable.FieldGet(getFSharpFieldName field, field.IsMutable, makeType com Map.empty field.FieldType)
        let typ = makeType com ctx.GenericArgs fsExpr.Type
        return Fable.Get(callee, kind, typ, makeRangeFrom fsExpr)

    | BasicPatterns.TupleGet(_tupleType, tupleElemIndex, tupleExpr) ->
        let! tupleExpr = transformExpr com ctx tupleExpr
        let typ = makeType com ctx.GenericArgs fsExpr.Type
        return Fable.Get(tupleExpr, Fable.TupleGet tupleElemIndex, typ, makeRangeFrom fsExpr)

    | BasicPatterns.UnionCaseGet (unionExpr, fsType, unionCase, field) ->
        let r = makeRangeFrom fsExpr
        let! unionExpr = transformExpr com ctx unionExpr
        match fsType with
        | ErasedUnion _ ->
            if unionCase.UnionCaseFields.Count = 1 then return unionExpr
            else
                let index = unionCase.UnionCaseFields |> Seq.findIndex (fun x -> x.Name = field.Name)
                return Fable.Get(unionExpr, Fable.TupleGet index, makeType com ctx.GenericArgs fsType, r)
        | StringEnum _ ->
            return "StringEnum types cannot have fields"
            |> addErrorAndReturnNull com ctx.InlinePath r
        | OptionUnion t ->
            return Fable.Get(unionExpr, Fable.OptionValue, makeType com ctx.GenericArgs t, r)
        | ListUnion t ->
            let t = makeType com ctx.GenericArgs t
            let kind, t =
                if field.Name = "Head"
                then Fable.ListHead, t
                else Fable.ListTail, Fable.List t
            return Fable.Get(unionExpr, kind, t, r)
        | DiscriminatedUnion _ ->
            let t = makeType com Map.empty field.FieldType
            let kind = Fable.UnionField(field, unionCase, t)
            let typ = makeType com ctx.GenericArgs fsExpr.Type
            return Fable.Get(unionExpr, kind, typ, r)

    | BasicPatterns.FSharpFieldSet(callee, calleeType, field, value) ->
        let! callee = transformExprOpt com ctx callee
        let! value = transformExpr com ctx value
        let callee =
            match callee with
            | Some callee -> callee
            | None -> entityRef com calleeType.TypeDefinition
        return Fable.Set(callee, Fable.FieldSet(getFSharpFieldName field, makeType com Map.empty field.FieldType), value, makeRangeFrom fsExpr)

    | BasicPatterns.UnionCaseTag(unionExpr, _unionType) ->
        let! unionExpr = transformExpr com ctx unionExpr
        return Fable.Get(unionExpr, Fable.UnionTag, Fable.Any, makeRangeFrom fsExpr)

    | BasicPatterns.UnionCaseSet (_unionExpr, _type, _case, _caseField, _valueExpr) ->
        return "Unexpected UnionCaseSet" |> addErrorAndReturnNull com ctx.InlinePath (makeRangeFrom fsExpr)

    | BasicPatterns.ValueSet (valToSet, valueExpr) ->
        let r = makeRangeFrom fsExpr
        let! valueExpr = transformExpr com ctx valueExpr
        match valToSet.DeclaringEntity with
        | Some ent when ent.IsFSharpModule && isPublicMember valToSet ->
            // Mutable and public module values are compiled as functions, because
            // values imported from ES2015 modules cannot be modified (see #986)
            let valToSet = makeValueFrom com ctx r valToSet
            return Fable.Operation(Fable.CurriedApply(valToSet, [valueExpr]), Fable.Unit, r)
        | _ ->
            let valToSet = makeValueFrom com ctx r valToSet
            return Fable.Set(valToSet, Fable.VarSet, valueExpr, r)

    // Instantiation
    | BasicPatterns.NewArray(FableType com ctx elTyp, argExprs) ->
        let! argExprs = transformExprList com ctx argExprs
        return makeArray elTyp argExprs

    | BasicPatterns.NewTuple(_tupleType, argExprs) ->
        let! argExprs = transformExprList com ctx argExprs
        return Fable.NewTuple(argExprs) |> makeValue (makeRangeFrom fsExpr)

    | BasicPatterns.ObjectExpr(objType, baseCall, overrides, otherOverrides) ->
        let objExpr ctx =
            transformObjExpr com ctx objType baseCall overrides otherOverrides
        match ctx.EnclosingMember with
        | Some m when m.IsImplicitConstructor ->
            let thisArg = getIdentUniqueName ctx "_this" |> makeIdent
            let thisValue = Fable.Value(Fable.ThisValue Fable.Any, None)
            let ctx = { ctx with BoundConstructorThis = Some thisArg }
            let! objExpr = transformObjExpr com ctx objType baseCall overrides otherOverrides
            return Fable.Let([thisArg, thisValue], objExpr)
        | _ -> return! transformObjExpr com ctx objType baseCall overrides otherOverrides

    | BasicPatterns.NewObject(memb, genArgs, args) ->
        // TODO: Check arguments passed byref here too?
        let! args = transformExprList com ctx args
        let genArgs = Seq.map (makeType com ctx.GenericArgs) genArgs
        let typ = makeType com ctx.GenericArgs fsExpr.Type
        return makeCallFrom com ctx (makeRangeFrom fsExpr) typ genArgs None args memb

    // work-around for optimized "for x in list" (erases this sequential)
    | BasicPatterns.Sequential (BasicPatterns.ValueSet (current, BasicPatterns.Value next1),
                                (BasicPatterns.ValueSet (next2, BasicPatterns.UnionCaseGet
                                    (_value, typ, unionCase, field))))
            when next1.FullName = "next" && next2.FullName = "next"
                && current.FullName = "current" && (getFsTypeFullName typ) = Types.list
                && unionCase.Name = "op_ColonColon" && field.Name = "Tail" ->
        // replace with nothing
        return Fable.UnitConstant |> makeValue None

    | BasicPatterns.Sequential (first, second) ->
        let! first = transformExpr com ctx first
        let! second = transformExpr com ctx second
        return Fable.Sequential [first; second]

    | BasicPatterns.NewRecord(fsType, argExprs) ->
        let! argExprs = transformExprList com ctx argExprs
        let genArgs = makeGenArgs com ctx.GenericArgs (getGenericArguments fsType)
        return Fable.NewRecord(argExprs, Fable.DeclaredRecord fsType.TypeDefinition, genArgs) |> makeValue (makeRangeFrom fsExpr)

    | BasicPatterns.NewAnonRecord(fsType, argExprs) ->
        let! argExprs = transformExprList com ctx argExprs
        let fieldNames = fsType.AnonRecordTypeDetails.SortedFieldNames
        let genArgs = makeGenArgs com ctx.GenericArgs (getGenericArguments fsType)
        return Fable.NewRecord(argExprs, Fable.AnonymousRecord fieldNames, genArgs) |> makeValue (makeRangeFrom fsExpr)

    | BasicPatterns.NewUnionCase(fsType, unionCase, argExprs) ->
        let! argExprs = transformExprList com ctx argExprs
        return argExprs
        |> transformNewUnion com ctx (makeRangeFrom fsExpr) fsType unionCase

    // Type test
    | BasicPatterns.TypeTest (FableType com ctx typ, expr) ->
        let! expr = transformExpr com ctx expr
        return Fable.Test(expr, Fable.TypeTest typ, makeRangeFrom fsExpr)

    | BasicPatterns.UnionCaseTest(unionExpr, fsType, unionCase) ->
        return! transformUnionCaseTest com ctx (makeRangeFrom fsExpr) unionExpr fsType unionCase

    // Pattern Matching
    | BasicPatterns.DecisionTree(decisionExpr, decisionTargets) ->
        let! fableDecisionExpr = transformExpr com ctx decisionExpr
        let! fableDecisionTargets = transformDecisionTargets com ctx [] decisionTargets

        // rewrite last decision target if it throws MatchFailureException
        let compiledFableTargets =
            match snd (List.last decisionTargets) with
            | RaisingMatchFailureExpr fileNameWhereErrorOccurs ->
                match decisionExpr with
                | BasicPatterns.IfThenElse(BasicPatterns.UnionCaseTest(_unionValue, unionType, _unionCaseInfo), _, _) ->
                    let rangeOfLastDecisionTarget = makeRangeFrom (snd (List.last decisionTargets))
                    let errorMessage =
                        sprintf "The match cases were incomplete against type of '%s' at %s"
                            unionType.TypeDefinition.DisplayName
                            fileNameWhereErrorOccurs
                    let errorExpr = Replacements.Helpers.error (Fable.Value(Fable.StringConstant errorMessage, None))
                    // Creates a "throw Error({errorMessage})" expression
                    let throwExpr = Fable.Throw(errorExpr, Fable.Any, rangeOfLastDecisionTarget)

                    fableDecisionTargets
                    |> List.replaceLast (fun _lastExpr -> [], throwExpr)

                | _ ->
                    // TODO: rewrite other `MatchFailureException` to `failwith "The match cases were incomplete"`
                    fableDecisionTargets

            | _ -> fableDecisionTargets

        return Fable.DecisionTree(fableDecisionExpr, compiledFableTargets)

    | BasicPatterns.DecisionTreeSuccess(targetIndex, boundValues) ->
        let! boundValues = transformExprList com ctx boundValues
        let typ = makeType com ctx.GenericArgs fsExpr.Type
        return Fable.DecisionTreeSuccess(targetIndex, boundValues, typ)

    | BasicPatterns.ILFieldGet(None, ownerTyp, fieldName) ->
        let ownerTyp = makeType com ctx.GenericArgs ownerTyp
        let typ = makeType com ctx.GenericArgs fsExpr.Type
        match Replacements.tryField typ ownerTyp fieldName with
        | Some expr -> return expr
        | None ->
            return sprintf "Cannot compile ILFieldGet(%A, %s)" ownerTyp fieldName
            |> addErrorAndReturnNull com ctx.InlinePath (makeRangeFrom fsExpr)

    | BasicPatterns.Quote _ ->
        return "Quotes are not currently supported by Fable"
        |> addErrorAndReturnNull com ctx.InlinePath (makeRangeFrom fsExpr)

    // TODO: Ask. I see this when accessing Result types (all structs?)
    | BasicPatterns.AddressOf(expr) ->
        let! expr = transformExpr com ctx expr
        return expr

    // | BasicPatterns.ILFieldSet _
    // | BasicPatterns.AddressSet _
    // | BasicPatterns.ILAsm _
    | expr ->
        return sprintf "Cannot compile expression %A" expr
        |> addErrorAndReturnNull com ctx.InlinePath (makeRangeFrom fsExpr)
  }

let private isIgnoredNonAttachedMember (meth: FSharpMemberOrFunctionOrValue) =
    Option.isSome meth.LiteralValue
    || meth.Attributes |> Seq.exists (fun att ->
        match att.AttributeType.TryFullName with
        | Some(Atts.global_ | Naming.StartsWith Atts.import _ | Naming.StartsWith Atts.emit _) -> true
        | _ -> false)
    || (match meth.DeclaringEntity with
        | Some ent -> isGlobalOrImportedEntity ent
        | None -> false)

let private isRecordLike (ent: FSharpEntity) =
    ent.IsFSharpRecord
        || ent.IsFSharpExceptionDeclaration
        || ((ent.IsClass || ent.IsValueType) && not ent.IsMeasure
                                             && not ent.IsEnum
                                             && not (hasImplicitConstructor ent))

let private transformImplicitConstructor com (ctx: Context)
            (memb: FSharpMemberOrFunctionOrValue) args (body: FSharpExpr) =
    match memb.DeclaringEntity with
    | None -> "Unexpected constructor without declaring entity: " + memb.FullName
              |> addError com ctx.InlinePath None; []
    | Some ent ->
        let mutable baseRefAndConsCall = None
        let captureBaseCall =
            ent.BaseType |> Option.bind (fun (NonAbbreviatedType baseType) ->
                if baseType.HasTypeDefinition then
                    let ent = baseType.TypeDefinition
                    match ent.TryFullName with
                    | Some name when name <> Types.object ->
                        Some(ent, fun c -> baseRefAndConsCall <- Some c)
                    | _ -> None
                else None)
        let bodyCtx, args = bindMemberArgs com ctx args
        let bodyCtx = { bodyCtx with CaptureBaseConsCall = captureBaseCall }
        let body = transformExpr com bodyCtx body |> run
        let consName, _ = getMemberDeclarationName com memb
        let entityName = getEntityDeclarationName com ent
        let r = getEntityLocation ent |> makeRange
        let info = Fable.ClassImplicitConstructorInfo(ent, consName, entityName,
                    args, body, baseRefAndConsCall, hasSeqSpread memb,
                    isPublicMember memb, isPublicEntity ent, r)
        [Fable.ClassImplicitConstructorDeclaration(info, set ctx.UseNamesInDeclarationScope)]

/// When using `importMember`, uses the member display name as selector
let private importExprSelector (memb: FSharpMemberOrFunctionOrValue) selector =
    match selector with
    | Fable.Value(Fable.StringConstant Naming.placeholder,_) ->
        getMemberDisplayName memb |> makeStrConst
    | _ -> selector

let private transformImport com r typ isMutable isPublic name selector path =
    if isMutable && isPublic then // See #1314
        "Imported members cannot be mutable and public, please make it private: " + name
        |> addError com [] None
    let info = Fable.ModuleMemberInfo(name, isValue=true, isPublic=isPublic, isMutable=isMutable)
    let fableValue = Fable.Import(selector, path, Fable.CustomImport, typ, r)
    [Fable.ModuleMemberDeclaration([], fableValue, info, Set.empty)]

let private transformMemberValue (com: IFableCompiler) ctx isPublic name (memb: FSharpMemberOrFunctionOrValue) (value: FSharpExpr) =
    let value = transformExpr com ctx value |> run
    match value with
    // Accept import expressions, e.g. let foo = import "foo" "myLib"
    | Fable.Import(selector, path, Fable.CustomImport, typ, r) ->
        match typ with
        | Fable.FunctionType(Fable.LambdaType _, Fable.FunctionType(Fable.LambdaType _, _)) ->
            "Change declaration of member: " + name + "\n"
            + "Importing JS functions with multiple arguments as `let add: int->int->int` won't uncurry parameters." + "\n"
            + "Use following syntax: `let add (x:int) (y:int): int = import ...`"
            |> addError com ctx.InlinePath None
        | _ -> ()
        let selector = importExprSelector memb selector
        transformImport com r typ memb.IsMutable isPublic name selector path
    | fableValue ->
        let r = makeRange memb.DeclarationLocation
        let info = Fable.ModuleMemberInfo(name, ?declaringEntity=memb.DeclaringEntity,
                    isValue=true, isPublic=isPublic, isMutable=memb.IsMutable, range=r)
        [Fable.ModuleMemberDeclaration([], fableValue, info, set ctx.UseNamesInDeclarationScope)]

let private moduleMemberDeclarationInfo name isValue isPublic (memb: FSharpMemberOrFunctionOrValue): Fable.ModuleMemberInfo =
    Fable.ModuleMemberInfo(name,
        ?declaringEntity=memb.DeclaringEntity,
        hasSpread=hasSeqSpread memb,
        isValue=isValue,
        isPublic=isPublic,
        isInstance=memb.IsInstanceMember,
        isMutable=memb.IsMutable,
        isEntryPoint=hasAttribute Atts.entryPoint memb.Attributes,
        range=makeRange memb.DeclarationLocation)

let private transformMemberFunction (com: IFableCompiler) ctx isPublic name (memb: FSharpMemberOrFunctionOrValue) args (body: FSharpExpr) =
    let bodyCtx, args = bindMemberArgs com ctx args
    let body = transformExpr com bodyCtx body |> run
    match body with
    // Accept import expressions, e.g. let foo x y = import "foo" "myLib"
    | Fable.Import(selector, path, Fable.CustomImport, _, r) ->
        // Use the full function type
        let typ = makeType com Map.empty memb.FullType
        let selector = importExprSelector memb selector
        transformImport com r typ false isPublic name selector path
    | body ->
        // If this is a static constructor, call it immediately
        if memb.CompiledName = ".cctor" then
            let fn = Fable.Function(Fable.Delegate args, body, Some name)
            let apply = makeCall None Fable.Unit (makeSimpleCallInfo None [] []) fn
            [Fable.ActionDeclaration(apply, set ctx.UseNamesInDeclarationScope)]
        else
            let info = moduleMemberDeclarationInfo name false isPublic memb
            [Fable.ModuleMemberDeclaration(args, body, info, set ctx.UseNamesInDeclarationScope)]

let private transformMemberFunctionOrValue (com: IFableCompiler) ctx (memb: FSharpMemberOrFunctionOrValue) args (body: FSharpExpr) =
    let isPublic = isPublicMember memb
    let name, _ = getMemberDeclarationName com memb
    match memb.Attributes with
    | ImportAtt(selector, path) ->
        let selector =
            if selector = Naming.placeholder then getMemberDisplayName memb
            else selector
        let typ = makeType com Map.empty memb.FullType
        transformImport com None typ memb.IsMutable isPublic name (makeStrConst selector) (makeStrConst path)
    | _ ->
        if isModuleValueForDeclarations memb
        then transformMemberValue com ctx isPublic name memb body
        else transformMemberFunction com ctx isPublic name memb args body

let private transformAttachedMember (com: FableCompiler) (ctx: Context)
            (declaringEntity: FSharpEntity) (signature: FSharpAbstractSignature)
            (memb: FSharpMemberOrFunctionOrValue) args (body: FSharpExpr) =
    let bodyCtx, args = bindMemberArgs com ctx args
    let body = transformExpr com bodyCtx body |> run
    let r = makeRange memb.DeclarationLocation |> Some
    let info = getAttachedMemberInfo com ctx r com.NonMangledAttachedMemberConflicts (Some declaringEntity) signature
    [Fable.AttachedMemberDeclaration(args, body, info, declaringEntity, set ctx.UseNamesInDeclarationScope)]

let private transformMemberDecl (com: FableCompiler) (ctx: Context) (memb: FSharpMemberOrFunctionOrValue)
                                (args: FSharpMemberOrFunctionOrValue list list) (body: FSharpExpr) =
    let ctx = { ctx with EnclosingMember = Some memb
                         UseNamesInDeclarationScope = HashSet() }
    if isIgnoredNonAttachedMember memb then
        if memb.IsMutable && isPublicMember memb && hasAttribute Atts.global_ memb.Attributes then
            "Global members cannot be mutable and public, please make it private: " + memb.DisplayName
            |> addError com [] None
        []
    elif isInline memb then
        let inlineExpr = { Args = List.concat args
                           Body = body
                           FileName = (com :> ICompiler).CurrentFile }
        com.AddInlineExpr(memb, inlineExpr)
        if com.Options.outputPublicInlinedFunctions && isPublicMember memb then
            transformMemberFunctionOrValue com ctx memb args body
        else []
    elif memb.IsImplicitConstructor then
        transformImplicitConstructor com ctx memb args body
    elif memb.IsOverrideOrExplicitInterfaceImplementation then
        // Ignore attached members generated by the F# compiler (for comparison and equality)
        if memb.IsCompilerGenerated then []
        else
            match memb.DeclaringEntity with
            | Some declaringEntity ->
                if isGlobalOrImportedEntity declaringEntity then []
                elif isErasedOrStringEnumEntity declaringEntity then
                    let r = makeRange memb.DeclarationLocation |> Some
                    "Erased types cannot implement abstract members"
                    |> addError com ctx.InlinePath r
                    []
                else
                    // Not sure when it's possible that a member implements multiple abstract signatures
                    memb.ImplementedAbstractSignatures |> Seq.tryHead
                    |> Option.map (fun s -> transformAttachedMember com ctx declaringEntity s memb args body)
                    |> Option.defaultValue []
            | None -> []
    else transformMemberFunctionOrValue com ctx memb args body

let private addUsedRootName com (usedRootNames: Set<string>) name =
    if Set.contains name usedRootNames then
        "Cannot have two module members with same name: " + name
        |> addError com [] None
    Set.add name usedRootNames

// In case this is a recursive module, do a first pass to get all entity and member names
let rec private getUsedRootNames com (usedNames: Set<string>) decls =
    (usedNames, decls) ||> List.fold (fun usedNames decl ->
        match decl with
        | FSharpImplementationFileDeclaration.Entity(ent, sub) ->
            if isErasedOrStringEnumEntity ent then usedNames
            elif ent.IsFSharpUnion || isRecordLike ent then
                getEntityDeclarationName com ent
                |> addUsedRootName com usedNames
            else
                getUsedRootNames com usedNames sub
        | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(memb,_,_) ->
            if memb.IsOverrideOrExplicitInterfaceImplementation then usedNames
            else
                let memberName, _ = getMemberDeclarationName com memb
                let usedNames = addUsedRootName com usedNames memberName
                match memb.DeclaringEntity with
                | Some ent when memb.IsImplicitConstructor ->
                    let entityName = getEntityDeclarationName com ent
                    addUsedRootName com usedNames entityName
                | _ -> usedNames
        | FSharpImplementationFileDeclaration.InitAction _ -> usedNames)

let rec private transformDeclarations (com: FableCompiler) ctx fsDecls =
    fsDecls |> List.collect (fun fsDecl ->
        match fsDecl with
        | FSharpImplementationFileDeclaration.Entity(ent, sub) ->
            if isErasedOrStringEnumEntity ent then []
            elif ent.IsFSharpUnion || isRecordLike ent then
                let entityName = getEntityDeclarationName com ent
                // TODO: Check Equality/Comparison attributes
                let r = getEntityLocation ent |> makeRange
                Fable.ConstructorInfo(ent, entityName, isPublicEntity ent, ent.IsFSharpUnion, r)
                |> Fable.CompilerGeneratedConstructorDeclaration
                |> List.singleton
            else
                transformDeclarations com { ctx with EnclosingEntity = Some ent } sub
        | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(meth, args, body) ->
            transformMemberDecl com ctx meth args body
        | FSharpImplementationFileDeclaration.InitAction fe ->
            let ctx = { ctx with UseNamesInDeclarationScope = HashSet() }
            let e = transformExpr com ctx fe |> run
            [Fable.ActionDeclaration(e, set ctx.UseNamesInDeclarationScope)])

let private getRootModuleAndDecls decls =
    let rec getRootModuleAndDeclsInner outerEnt decls =
        match decls with
        | [FSharpImplementationFileDeclaration.Entity (ent, decls)]
                when ent.IsFSharpModule || ent.IsNamespace ->
            getRootModuleAndDeclsInner (Some ent) decls
        | CommonNamespace(ent, decls) ->
            getRootModuleAndDeclsInner (Some ent) decls
        | decls -> outerEnt, decls
    getRootModuleAndDeclsInner None decls

let private tryGetMemberArgsAndBody com (implFiles: IDictionary<string, FSharpImplementationFileContents>)
                                    fileName entityFullName memberUniqueName =
    let rec tryGetMemberArgsAndBodyInner (entityFullName: string) (memberUniqueName: string) = function
        | FSharpImplementationFileDeclaration.Entity (e, decls) ->
            let entityFullName2 = getEntityFullName e
            if entityFullName.StartsWith(entityFullName2)
            then List.tryPick (tryGetMemberArgsAndBodyInner entityFullName memberUniqueName) decls
            else None
        | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue (memb2, args, body) ->
            if getMemberUniqueName com memb2 = memberUniqueName
            then Some(args, body)
            else None
        | FSharpImplementationFileDeclaration.InitAction _ -> None
    match implFiles.TryGetValue(fileName) with
    | true, f -> f.Declarations |> List.tryPick (tryGetMemberArgsAndBodyInner entityFullName memberUniqueName)
    | false, _ -> None

type FableCompiler(com: ICompiler, implFiles: IDictionary<string, FSharpImplementationFileContents>) =
    member val InlineDependencies = HashSet<string>()
    member val NonMangledAttachedMemberNames = Dictionary<string, HashSet<string>>()
    member __.Options = com.Options

    member __.AddInlineExpr(memb, inlineExpr: InlineExpr) =
        let fullName = getMemberUniqueName com memb
        com.GetOrAddInlineExpr(fullName, fun () -> inlineExpr) |> ignore

    member this.NonMangledAttachedMemberConflicts declaringEntityName memberName =
        match this.NonMangledAttachedMemberNames.TryGetValue(declaringEntityName) with
        | true, memberNames -> memberNames.Add(memberName) |> not
        | false, _ -> this.NonMangledAttachedMemberNames.Add(declaringEntityName, HashSet [|memberName|]); false

    interface IFableCompiler with
        member this.Transform(ctx, fsExpr) =
            transformExpr this ctx fsExpr |> run

        member this.TryReplace(ctx, r, t, info, thisArg, args) =
            Replacements.tryCall this ctx r t info thisArg args

        member this.InjectArgument(ctx, r, genArgs, parameter) =
            Inject.injectArg this ctx r genArgs parameter

        member this.GetInlineExpr(memb) =
            let membUniqueName = getMemberUniqueName com memb
            match memb.DeclaringEntity with
            | None -> failwith ("Unexpected inlined member without declaring entity. Please report: " + membUniqueName)
            | Some ent ->
                // The entity name is not included in the member unique name
                // for type extensions, see #1667
                let entFullName = getEntityFullName ent
                let fileName =
                    (getMemberLocation memb).FileName
                    |> Path.normalizePathAndEnsureFsExtension
                if fileName <> com.CurrentFile then
                    (this :> IFableCompiler).AddInlineDependency(fileName)
                com.GetOrAddInlineExpr(membUniqueName, fun () ->
                    match tryGetMemberArgsAndBody com implFiles fileName entFullName membUniqueName with
                    | Some(args, body) ->
                        { Args = List.concat args
                          Body = body
                          FileName = fileName }
                    | None -> failwith ("Cannot find inline member. Please report: " + membUniqueName))

        member this.TryGetImplementationFile (fileName) =
            let fileName = Path.normalizePathAndEnsureFsExtension fileName
            match implFiles.TryGetValue(fileName) with
            | true, f -> Some f
            | false, _ -> None

        member this.AddInlineDependency(fileName) =
            this.InlineDependencies.Add(fileName) |> ignore

    interface ICompiler with
        member __.Options = com.Options
        member __.LibraryDir = com.LibraryDir
        member __.CurrentFile = com.CurrentFile
        member __.GetRootModule(fileName) =
            com.GetRootModule(fileName)
        member __.GetOrAddInlineExpr(fullName, generate) =
            com.GetOrAddInlineExpr(fullName, generate)
        member __.AddLog(msg, severity, ?range, ?fileName:string, ?tag: string) =
            com.AddLog(msg, severity, ?range=range, ?fileName=fileName, ?tag=tag)

let getRootModuleFullName (file: FSharpImplementationFileContents) =
    let rootEnt, _ = getRootModuleAndDecls file.Declarations
    match rootEnt with
    | Some rootEnt -> getEntityFullName rootEnt
    | None -> ""

let transformFile (com: ICompiler) (implFiles: IDictionary<string, FSharpImplementationFileContents>) =
    let file =
        match implFiles.TryGetValue(com.CurrentFile) with
        | true, file -> file
        | false, _ ->
            let projFiles = implFiles |> Seq.map (fun kv -> kv.Key) |> String.concat "\n"
            failwithf "File %s cannot be found in source list:\n%s" com.CurrentFile projFiles
    let rootEnt, rootDecls = getRootModuleAndDecls file.Declarations
    let fcom = FableCompiler(com, implFiles)
    let usedRootNames = getUsedRootNames com Set.empty rootDecls
    let ctx = Context.Create(rootEnt, usedRootNames)
    let rootDecls = transformDeclarations fcom ctx rootDecls
    Fable.File(com.CurrentFile, rootDecls, usedRootNames, set fcom.InlineDependencies)
