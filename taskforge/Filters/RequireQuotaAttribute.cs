using System;
using Microsoft.AspNetCore.Mvc;

namespace taskforge.Filters;

/// <summary>
/// Требует списание квоты перед выполнением action.
/// Возвращает 429 если квота исчерпана.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class RequireQuotaAttribute : TypeFilterAttribute
{
    public RequireQuotaAttribute(string bucket) : base(typeof(RequireQuotaFilter))
    {
        Arguments = new object[] { bucket };
    }
}
