using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Wasla.Api.Controllers;

/// <summary>
/// Binds a string parameter from the query string exactly as submitted (WAS-182 report filter). MVC's default string
/// binding turns an empty or whitespace-only value into null, so <c>platform=%20</c> would look omitted. With this
/// attribute an omitted parameter stays null, an empty one is <c>""</c>, and any other value keeps its text, spaces
/// included, for the action to validate. With repeated values the first is bound, as by default; the action detects the
/// repetition itself. Applies only where it is placed; global model binding is unchanged.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromQueryAsSubmittedAttribute : ModelBinderAttribute
{
    public FromQueryAsSubmittedAttribute()
        : base(typeof(Binder))
    {
        BindingSource = BindingSource.Query;
    }

    private sealed class Binder : IModelBinder
    {
        public Task BindModelAsync(ModelBindingContext bindingContext)
        {
            if (bindingContext.ModelType != typeof(string))
                throw new InvalidOperationException($"{nameof(FromQueryAsSubmittedAttribute)} binds string parameters only.");

            var value = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
            if (value == ValueProviderResult.None)
                return Task.CompletedTask;

            bindingContext.ModelState.SetModelValue(bindingContext.ModelName, value);
            bindingContext.Result = ModelBindingResult.Success(value.FirstValue);
            return Task.CompletedTask;
        }
    }
}
