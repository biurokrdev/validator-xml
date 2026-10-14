var interpreter = new Interpreter()
    .Reference(ctx.BindingContext.DynamicExpressoTypeReferences.Select(type => new ReferenceType(type)))
    .Reference(typeof(NumberFormats))
    .SetVariable("formModel", ctx.FormModel)
    .SetVariable("docTree", ctx.DocTree)
    .SetVariable("parentForm", ctx.FormModel?.ParentForm);

var builder = new RecordTableBuilder(new RecordTableOptions
{
    Logger = logger,
    ExpressionEvaluator = new DelegateExpressionEvaluator((expr, scope) =>
    {
        interpreter.SetVariable("record", scope.Current);   // JObject – w wyrażeniu record["pole"]
        interpreter.SetVariable("index", scope.Index);
        interpreter.SetVariable("root", scope.Root);
        return interpreter.Eval(expr);
    }),
});