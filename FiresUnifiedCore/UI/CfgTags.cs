using System;
using System.ComponentModel;
using System.Reflection;
using BepInEx.Configuration;

namespace FiresCore.UI
{
    /// <summary>
    /// ConfigurationManager-compatible read of a setting's <see cref="ConfigDescription.Tags"/>. Mods annotate
    /// their settings for ConfigurationManager with System.ComponentModel attributes, bare strings, or a
    /// duck-typed <c>ConfigurationManagerAttributes</c> object; all three are honoured here so a mod written
    /// for any BepInEx config manager renders the same in the Fires window.
    /// </summary>
    public sealed class CfgTags
    {
        private const string AttributeTypeName = "ConfigurationManagerAttributes";
        private const string AdvancedTagName = "Advanced";
        private const string ReadOnlyTagName = "ReadOnly";
        private const string BrowsableTagName = "Browsable";
        private const string UnbrowsableTagName = "Unbrowsable";
        private const string HiddenTagName = "Hidden";

        private const BindingFlags PublicInstance = BindingFlags.Instance | BindingFlags.Public;

        public static readonly CfgTags None = new CfgTags();

        public bool Browsable = true;
        public bool ReadOnly;
        public bool IsAdvanced;
        public bool ShowRangeAsPercent;
        public bool HideDefaultButton;
        public bool HideSettingName;
        public string Category;
        public string DispName;
        public string Description;
        public object DefaultValue;
        public int Order;
        public Action<ConfigEntryBase> CustomDrawer;

        private object _attribute;
        private MemberInfo _attributeReadOnly;

        public static CfgTags Read(ConfigDescription description)
        {
            object[] tags = description?.Tags;
            if (tags == null || tags.Length == 0) return None;

            var read = new CfgTags();
            foreach (object tag in tags)
            {
                switch (tag)
                {
                    case null: continue;
                    case DisplayNameAttribute displayName: read.DispName = displayName.DisplayName; continue;
                    case CategoryAttribute category: read.Category = category.Category; continue;
                    case DescriptionAttribute descriptionAttribute: read.Description = descriptionAttribute.Description; continue;
                    case DefaultValueAttribute defaultValue: read.DefaultValue = defaultValue.Value; continue;
                    case ReadOnlyAttribute readOnly: read.ReadOnly = readOnly.IsReadOnly; continue;
                    case BrowsableAttribute browsable: read.Browsable = browsable.Browsable; continue;
                    case Action<ConfigEntryBase> drawer: read.CustomDrawer = drawer; continue;
                    case string name: read.ApplyStringTag(name); continue;
                    default:
                        if (tag.GetType().Name == AttributeTypeName) read.CopyFromAttributeObject(tag);
                        continue;
                }
            }
            return read;
        }

        /// <summary>
        /// ConfigSync flips <c>ReadOnly</c> on its own attribute object whenever the server lock state or the
        /// caller's admin status changes, so the flag has to be re-read rather than captured at discovery.
        /// </summary>
        public bool IsReadOnlyNow()
        {
            if (_attributeReadOnly == null) return ReadOnly;
            try
            {
                object value = _attributeReadOnly is FieldInfo field
                    ? field.GetValue(_attribute)
                    : ((PropertyInfo)_attributeReadOnly).GetValue(_attribute, null);
                return value as bool? ?? ReadOnly;
            }
            catch { return ReadOnly; }
        }

        private void ApplyStringTag(string name)
        {
            switch (name)
            {
                case ReadOnlyTagName: ReadOnly = true; break;
                case BrowsableTagName: Browsable = true; break;
                case UnbrowsableTagName:
                case HiddenTagName: Browsable = false; break;
                case AdvancedTagName: IsAdvanced = true; break;
            }
        }

        private void CopyFromAttributeObject(object attribute)
        {
            var type = attribute.GetType();

            Browsable = MemberValue(type, attribute, nameof(Browsable)) as bool? ?? Browsable;
            ReadOnly = MemberValue(type, attribute, nameof(ReadOnly)) as bool? ?? ReadOnly;
            IsAdvanced = MemberValue(type, attribute, nameof(IsAdvanced)) as bool? ?? IsAdvanced;
            ShowRangeAsPercent = MemberValue(type, attribute, nameof(ShowRangeAsPercent)) as bool? ?? ShowRangeAsPercent;
            HideDefaultButton = MemberValue(type, attribute, nameof(HideDefaultButton)) as bool? ?? HideDefaultButton;
            HideSettingName = MemberValue(type, attribute, nameof(HideSettingName)) as bool? ?? HideSettingName;
            Order = MemberValue(type, attribute, nameof(Order)) as int? ?? Order;
            Category = MemberValue(type, attribute, nameof(Category)) as string ?? Category;
            DispName = MemberValue(type, attribute, nameof(DispName)) as string ?? DispName;
            Description = MemberValue(type, attribute, nameof(Description)) as string ?? Description;
            DefaultValue = MemberValue(type, attribute, nameof(DefaultValue)) ?? DefaultValue;
            CustomDrawer = MemberValue(type, attribute, nameof(CustomDrawer)) as Action<ConfigEntryBase> ?? CustomDrawer;

            _attribute = attribute;
            _attributeReadOnly = (MemberInfo)type.GetField(nameof(ReadOnly), PublicInstance)
                ?? type.GetProperty(nameof(ReadOnly), PublicInstance);
        }

        private static object MemberValue(Type type, object instance, string name)
        {
            try
            {
                var property = type.GetProperty(name, PublicInstance);
                if (property != null && property.CanRead) return property.GetValue(instance, null);
                return type.GetField(name, PublicInstance)?.GetValue(instance);
            }
            catch { return null; }
        }
    }
}
