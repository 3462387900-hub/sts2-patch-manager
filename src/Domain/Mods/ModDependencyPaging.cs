using System;
using System.Collections.Generic;
using System.Linq;

namespace SpirePatch;

// Pure paging logic: keep statistics based on the complete report, but render
// only the active page. A new filter starts at page one.
internal static class ModDependencyPaging
{
 internal const int PageSize=10;
 internal static int Count(int total)=>Math.Max(1,(total+PageSize-1)/PageSize);
 internal static int Clamp(int requested,int total)=>Math.Clamp(requested,1,Count(total));
 internal static T[] Items<T>(IReadOnlyList<T> values,int page)
 {
  int selected=Clamp(page,values.Count);
  return values.Skip((selected-1)*PageSize).Take(PageSize).ToArray();
 }
}
