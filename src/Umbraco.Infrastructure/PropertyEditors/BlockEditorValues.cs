// Copyright (c) Umbraco.
// See LICENSE for more details.

using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Cache.PropertyEditors;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Blocks;
using Umbraco.Extensions;

namespace Umbraco.Cms.Core.PropertyEditors;

/// <summary>
/// Used to deserialize json values and clean up any values based on the existence of element types and layout structure
/// </summary>
internal class BlockEditorValues<TValue, TLayout>
    where TValue : BlockValue<TLayout>, new()
    where TLayout : class, IBlockLayoutItem, new()
{
    private readonly BlockEditorDataConverter<TValue, TLayout> _dataConverter;
    private readonly IBlockEditorElementTypeCache _elementTypeCache;
    private readonly ILogger _logger;

    public BlockEditorValues(BlockEditorDataConverter<TValue, TLayout> dataConverter, IBlockEditorElementTypeCache elementTypeCache, ILogger logger)
    {
        _dataConverter = dataConverter;
        _elementTypeCache = elementTypeCache;
        _logger = logger;
    }

    public BlockEditorData<TValue, TLayout>? DeserializeAndClean(object? propertyValue)
    {
        var propertyValueAsString = propertyValue?.ToString();
        if (string.IsNullOrWhiteSpace(propertyValueAsString))
        {
            return null;
        }

        BlockEditorData<TValue, TLayout> blockEditorData = _dataConverter.Deserialize(propertyValueAsString);
        return Clean(blockEditorData);
    }

    public BlockEditorData<TValue, TLayout>? ConvertAndClean(TValue blockValue)
    {
        BlockEditorData<TValue, TLayout> blockEditorData = _dataConverter.Convert(blockValue);
        return Clean(blockEditorData);
    }

    private BlockEditorData<TValue, TLayout>? Clean(BlockEditorData<TValue, TLayout> blockEditorData)
    {
        if (blockEditorData.BlockValue.ContentData.Count == 0)
        {
            // if there's no content ensure there's no settings too
            blockEditorData.BlockValue.SettingsData.Clear();
            return null;
        }

        var contentTypePropertyTypes = new Dictionary<string, Dictionary<string, IPropertyType>>();

        // filter out any content that isn't referenced in the layout references
        IEnumerable<Guid> contentTypeKeys = blockEditorData.BlockValue.ContentData.Select(x => x.ContentTypeKey)
            .Union(blockEditorData.BlockValue.SettingsData.Select(x => x.ContentTypeKey)).Distinct();
        IDictionary<Guid, IContentType> contentTypesDictionary = _elementTypeCache.GetAll(contentTypeKeys).ToDictionary(x=>x.Key);

        foreach (BlockItemData block in blockEditorData.BlockValue.ContentData.Where(x =>
                     blockEditorData.References.Any(r => x.Udi is not null && r.ContentUdi == x.Udi)))
        {
            ResolveBlockItemData(block, contentTypePropertyTypes, contentTypesDictionary);
        }

        // filter out any settings that isn't referenced in the layout references
        foreach (BlockItemData block in blockEditorData.BlockValue.SettingsData.Where(x =>
                     blockEditorData.References.Any(r =>
                         r.SettingsUdi is not null && x.Udi is not null && r.SettingsUdi == x.Udi)))
        {
            ResolveBlockItemData(block, contentTypePropertyTypes, contentTypesDictionary);
        }

        // remove blocks that couldn't be resolved
        blockEditorData.BlockValue.ContentData.RemoveAll(x => x.ContentTypeAlias.IsNullOrWhiteSpace());
        blockEditorData.BlockValue.SettingsData.RemoveAll(x => x.ContentTypeAlias.IsNullOrWhiteSpace());

        return blockEditorData;
    }


    private bool ResolveBlockItemData(BlockItemData block, Dictionary<string, Dictionary<string, IPropertyType>> contentTypePropertyTypes, IDictionary<Guid, IContentType> contentTypesDictionary)
    {
        if (contentTypesDictionary.TryGetValue(block.ContentTypeKey, out IContentType? contentType) is false)
        {
            return false;
        }

        // get the prop types for this content type but keep a dictionary of found ones so we don't have to keep re-looking and re-creating
        // objects on each iteration.
        if (!contentTypePropertyTypes.TryGetValue(contentType.Alias, out Dictionary<string, IPropertyType>? propertyTypes))
        {
            propertyTypes = contentTypePropertyTypes[contentType.Alias] = contentType.CompositionPropertyTypes.ToDictionary(x => x.Alias, x => x);
        }

        var propValues = new Dictionary<string, BlockItemData.BlockPropertyValue>();

        // find any keys that are not real property types and remove them
        foreach (KeyValuePair<string, object?> prop in block.RawPropertyValues.ToList())
        {
            // doesn't exist so remove it
            if (!propertyTypes.TryGetValue(prop.Key, out IPropertyType? propType))
            {
                block.RawPropertyValues.Remove(prop.Key);
                _logger.LogWarning(
                    "The property {PropertyKey} for block {BlockKey} was removed because the property type {PropertyTypeAlias} was not found on {ContentTypeAlias}",
                    prop.Key,
                    block.Key,
                    prop.Key,
                    contentType.Alias);
            }
            else
            {
                // set the value to include the resolved property type
                propValues[prop.Key] = new BlockItemData.BlockPropertyValue(prop.Value, propType);
            }
        }

        block.ContentTypeAlias = contentType.Alias;
        block.PropertyValues = propValues;

        return true;
    }
}
