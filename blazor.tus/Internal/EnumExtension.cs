using System;
using System.ComponentModel;

namespace blazor.tus.Internal;

internal static class EnumExtension
{
    internal static string GetEnumDescription(this Enum enumValue)  
    {  
        var fieldInfo = enumValue.GetType().GetField(enumValue.ToString());  
  
        var descriptionAttributes = (DescriptionAttribute[])fieldInfo.GetCustomAttributes(typeof(DescriptionAttribute), false);  
  
        return descriptionAttributes.Length > 0 ? descriptionAttributes[0].Description : enumValue.ToString();  
    }
}