﻿// -------------------------------------------------------------------------------------------
// <copyright file="TableEditorPresenter.cs" company="MapWindow OSS Team - www.mapwindow.org">
//  MapWindow OSS Team - 2015
// </copyright>
// -------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using MW5.Api.Interfaces;
using MW5.Api.Legend.Abstract;
using MW5.Attributes.Views;
using MW5.Plugins.Concrete;
using MW5.Plugins.Enums;
using MW5.Plugins.Interfaces;
using MW5.Plugins.Mvp;
using MW5.Plugins.Services;
using MW5.Plugins.TableEditor.Helpers;
using MW5.Plugins.TableEditor.Model;
using MW5.Plugins.TableEditor.Views.Abstract;
using MW5.Shared;

namespace MW5.Plugins.TableEditor.Views
{
    [HasRegions]
    internal class TableEditorPresenter : CommandDispatcher<ITableEditorView, TableEditorCommand>, IDockPanelPresenter
    {
        private readonly IAppContext _context;
        private readonly IFileDialogService _dialogService;
        private readonly ILayerService _layerService;
        private readonly Dictionary<int, TablePanelInfo> _tables;

        #region Constructors

        public TableEditorPresenter(IAppContext context, ITableEditorView view, IFileDialogService dialogService, ILayerService layerService)
            : base(view)
        {
            if (context == null) throw new ArgumentNullException("context");
            if (dialogService == null) throw new ArgumentNullException("dialogService");
            if (layerService == null) throw new ArgumentNullException("layerService");

            _context = context;
            _dialogService = dialogService;
            _layerService = layerService;

            _tables = new Dictionary<int, TablePanelInfo>();

            View.Initialize(context);

            View.Panels.BeforePanelClosed += (s, e) => CloseTable(e.LayerHandle);
            View.Panels.PanelActivated += (s, e) => View.OnActivateDockingPanel();
        }

        #endregion

        #region Properties

        public IAttributeTable ActiveTable
        {
            get
            {
                var fs = FeatureSet;

                if (fs == null)
                {
                    return null;
                }

                return fs.Table;
            }
        }

        public IFeatureSet FeatureSet
        {
            get { return View.ActiveFeatureSet; }
        }

        #endregion

        #region Public Methods

        public bool CheckAndSaveChanges(int layerHandle, bool withCancel)
        {
            var layer = _context.Legend.Layers.ItemByHandle(layerHandle);

            if (layer == null)
            {
                return true;
            }

            var fs = layer.FeatureSet;

            if (fs == null || !fs.EditingTable)
            {
                return true; // nothing to save
            }

            var result = PromptSaveChanges(withCancel, layer.Name);

            switch (result)
            {
                case DialogResult.Cancel:
                    return false;
                case DialogResult.Yes:
                    RunCommand(TableEditorCommand.SaveChanges);
                    break;
            }

            return true;
        }

        public void CloseTable(int layerHandle)
        {
            if (_tables.ContainsKey(layerHandle))
            {
                var info = _tables[layerHandle];

                View.Panels.Remove(info.Panel);
                _tables.Remove(layerHandle);

                if (info.FindReplace != null)
                {
                    info.FindReplace.View.ForceClose();
                }
            }

            View.UpdateView();
        }

        public Control GetInternalObject()
        {
            return View as Control;
        }

        public bool HasLayer(int layerHandle)
        {
            return _tables.ContainsKey(layerHandle);
        }

        public void OnViewSelectionChanged(int layerHandle)
        {
            UpdateSelection(layerHandle);

            _context.Map.Redraw();
        }

        public void OpenTable(ILegendLayer layer)
        {
            if (layer == null) throw new ArgumentNullException("layer");

            if (_tables.ContainsKey(layer.Handle))
            {
                View.Panels.ActivatePanel(layer.Handle);
                return;
            }

            CreateNewTable(layer);

            View.UpdateView();
        }

        private bool ValidateColumnIndex(TableEditorCommand command)
        {
            switch (command)
            {
                case TableEditorCommand.FieldSortAsc:
                case TableEditorCommand.FieldSortDesc:
                case TableEditorCommand.FieldStats:
                case TableEditorCommand.FieldProperties:
                case TableEditorCommand.CalculateField:
                case TableEditorCommand.RemoveField:
                    if (View.ActiveColumnIndex == -1)
                    {
                        MessageService.Current.Info("Active column is expected for this command");
                        return false;
                    }
                    break;
            }

            return true;
        }

        public override void RunCommand(TableEditorCommand command)
        {
            if (HandleLayout(command))
            {
                return;
            }

            var table = ActiveTable;
            if (table == null)
            {
                return;
            }

            if (!ValidateColumnIndex(command))
            {
                return;
            }

            switch (command)
            {
                case TableEditorCommand.Query:
                    var layer = _context.Layers.ItemByHandle(View.ActiveLayerHandle);
                    if (layer != null)
                    {
                        var model = new QueryBuilderModel(layer, string.Empty);
                        _context.Container.Run<QueryBuilderPresenter, QueryBuilderModel>(model);
                    }
                    break;
                case TableEditorCommand.ExportSelected:
                    var fs = View.ActiveFeatureSet;
                    if (fs != null)
                    {
                        fs.ExportSelected(_dialogService, _layerService);
                    }
                    break;
                case TableEditorCommand.StopJoins:
                    if (!table.Joins.Any())
                    {
                        MessageService.Current.Info("There are no joins to stop.");
                        return;
                    }
                    
                    if (MessageService.Current.Ask("Do you want to stop all joins for this table?"))
                    {
                        table.StopAllJoins();
                        View.UpdateDatasource();
                    }
                    break;
                case TableEditorCommand.ImportFieldDefinitions:
                    if (DbfImportHelper.ImportFieldsFromDbf(_dialogService, table))
                    {
                        View.UpdateDatasource();
                    }
                    break;
                case TableEditorCommand.ReloadTable:
                    View.UpdateDatasource();
                    break;
                case TableEditorCommand.ShowAliases:
                    AppConfig.Instance.TableEditorShowAliases = !AppConfig.Instance.TableEditorShowAliases;
                    View.UpdateDatasource();
                    break;
                case TableEditorCommand.ShowAllFields:
                    if (table.Fields.Any(f => !f.Visible))
                    {
                        if (MessageService.Current.Ask("Do you want to show hidden fields?"))
                        {
                            foreach (var fld in table.Fields)
                            {
                                fld.Visible = true;
                            }

                            View.UpdateDatasource();
                        }
                    }
                    else
                    {
                        MessageService.Current.Info("All fields are already visible.");
                    }
                    break;
                case TableEditorCommand.FieldSortAsc:
                case TableEditorCommand.FieldSortDesc:
                    {
                        var grid = View.ActiveGrid;
                        if (grid != null)
                        {
                            grid.SortByColumn(View.ActiveColumnIndex, command == TableEditorCommand.FieldSortAsc);
                        }
                    }
                    break;
                case TableEditorCommand.FieldHide:
                    {
                        var grid = View.ActiveGrid;
                        if (grid != null)
                        {
                            var fld = grid.GetField(View.ActiveColumnIndex);
                            var cmn = grid.Columns[View.ActiveColumnIndex];
                            if (fld != null && cmn != null)
                            {
                                cmn.Visible = false;
                                fld.Visible = false;
                            }
                        }
                    }
                    break;
                case TableEditorCommand.RemoveField:
                    {
                        var grid = View.ActiveGrid;
                        if (grid != null)
                        {
                            var fld = grid.GetField(View.ActiveColumnIndex);

                            if (MessageService.Current.Ask("Do you want to remove field: " + fld.Name + "?"))
                            {
                                var fields = table.Fields;
                                int index = fields.IndexOf(fld);

                                if (fields.Remove(index))
                                {
                                    View.UpdateDatasource();
                                }    
                            }
                        }
                    }
                    break;
                case TableEditorCommand.FieldProperties:
                case TableEditorCommand.AddField:
                    {
                        var grid = View.ActiveGrid;
                        if (grid != null)
                        {
                            bool newField = command == TableEditorCommand.AddField;
                            var fld = newField ? null : grid.GetField(View.ActiveColumnIndex);
                            var model = new FieldPropertiesModel(table, fld, newField);

                            if (_context.Container.Run<FieldPropertiesPresenter, FieldPropertiesModel>(model))
                            {
                                View.UpdateDatasource();
                            }
                        }
                    }
                    break;
                case TableEditorCommand.FieldStats:
                    {
                        var grid = View.ActiveGrid;
                        if (grid != null)
                        {
                            var fld = grid.GetField(View.ActiveColumnIndex);
                            if (fld.Type == Api.Enums.AttributeType.String)
                            {
                                MessageService.Current.Info("Statistics aren't available for text fields.");
                                return;
                            }

                            var model = new FieldStatsModel(table, fld.Index);
                            _context.Container.Run<FieldStatsPresenter, FieldStatsModel>(model);
                        }
                    }
                    break;
                case TableEditorCommand.UpdateMeasurements:
                    if (_context.Container.Run<UpdateMeasurementsPresenter, IFeatureSet>(View.ActiveFeatureSet))
                    {
                        View.UpdateDatasource();
                    }
                    break;
                case TableEditorCommand.Join:
                    _context.Container.Run<JoinsPresenter, IAttributeTable>(table);
                    View.UpdateDatasource();
                    break;
                case TableEditorCommand.StartEdit:
                    if (!table.EditMode)
                    {
                        table.StartEditing();

                        var grid = View.ActiveGrid;
                        if (grid != null)
                        {
                            grid.ReadOnly = !table.EditMode;
                        }
                    }

                    View.UpdateView();
                    break;
                case TableEditorCommand.SaveChanges:
                    if (table.EditMode)
                    {
                        var result = MessageService.Current.AskWithCancel(
                            "Do you want to save attribute table changes?");

                        if (result == DialogResult.Cancel)
                        {
                            return;
                        }

                        table.StopEditing(result == DialogResult.Yes);

                        var grid = View.ActiveGrid;
                        if (grid != null)
                        {
                            grid.ReadOnly = !table.EditMode;
                            View.UpdateDatasource();
                        }
                    }

                    View.UpdateView();
                    break;
                case TableEditorCommand.CalculateField:
                    {
                        var model = new FieldCalculatorModel(table, table.Fields[View.ActiveColumnIndex]);
                        if (_context.Container.Run<FieldCalculatorPresenter, FieldCalculatorModel>(model))
                        {
                            View.UpdateDatasource();
                        }
                    }
                    break;
                
                case TableEditorCommand.RemoveFields:
                    if (_context.Container.Run<DeleteFieldsPresenter, IAttributeTable>(table))
                    {
                        View.UpdateDatasource();
                    }
                    break;
                case TableEditorCommand.ShowSelected:
                    ShowSelected();
                    break;
                case TableEditorCommand.ZoomToCurrentCell:
                    {
                        var grid = View.ActiveGrid;
                        if (grid != null && grid.CurrentCell != null)
                        {
                            int rowIndex = grid.CurrentCell.RowIndex;
                            if (rowIndex != -1)
                            {
                                int shapeIndex = grid.RowManager.RealIndex(rowIndex);
                                _context.Map.ZoomToShape(View.ActiveLayerHandle, shapeIndex);
                                grid.Focus();
                            }
                        }
                        else
                        {
                            MessageService.Current.Info("No shape is currently being edited.");
                        }
                    }
                    break;
                case TableEditorCommand.ZoomToSelected:
                    {
                        _context.Map.ZoomToSelected(View.ActiveLayerHandle);
                        var grid = View.ActiveGrid;
                        if (grid != null)
                        {
                            grid.Focus();
                        }
                    }
                    break;
                case TableEditorCommand.SelectAll:
                    FeatureSet.SelectAll();
                    OnViewSelectionChanged(View.ActiveLayerHandle);
                    break;

                case TableEditorCommand.ClearSelection:
                    FeatureSet.ClearSelection();
                    OnViewSelectionChanged(View.ActiveLayerHandle);
                    break;

                case TableEditorCommand.InvertSelection:
                    FeatureSet.InvertSelection();
                    OnViewSelectionChanged(View.ActiveLayerHandle);
                    break;
                case TableEditorCommand.Find:
                    Find(false);
                    break;
                case TableEditorCommand.Replace:
                    Find(true);
                    break;
            }
        }

        public void UpdateSelection(int layerHandle)
        {
            var info = GetTableInfo(layerHandle);
            if (info == null) return;

            info.Grid.Invalidate();

            View.UpdatePanelCaption(layerHandle);

            View.OnSelectionChanged();
        }

        #endregion

        #region Methods

        private void CreateNewTable(ILegendLayer layer)
        {
            var info = View.CreateTablePanel(layer);
            _tables.Add(layer.Handle, info);
            info.Grid.SelectionChanged += (s, e) => OnViewSelectionChanged(layer.Handle);
        }

        private void Find(bool replace)
        {
            var info = GetTableInfo(View.ActiveLayerHandle);
            if (info == null) return;

            if (info.FindReplace != null && info.FindReplace.Model.Replace != replace)
            {
                info.FindReplace.View.Close();
                info.FindReplace = null;
            }

            if (info.FindReplace == null)
            {
                info.FindReplace = _context.Container.GetInstance<FindReplacePresenter>();
            }

            var model = new FindReplaceModel(info.Grid, info.Layer, replace);

            info.FindReplace.Run(model);
        }

        private TablePanelInfo GetTableInfo(int layerHandle)
        {
            TablePanelInfo info;
            _tables.TryGetValue(layerHandle, out info);

            return info;
        }

        private bool HandleLayout(TableEditorCommand command)
        {
            if (command != TableEditorCommand.LayoutHorizontal && command != TableEditorCommand.LayoutVertical
                && command != TableEditorCommand.LayoutTabbed)
            {
                return false;
            }

            SaveLayoutOption(command);

            int size;
            DockPanelState state;

            View.GetLayoutSpecs(AppConfig.Instance.TableEditorLayout, out size, out state);

            var list = View.Panels.ToList().OrderBy(p => p.Caption);
            if (list.Count() < 2)
            {
                return true;
            }

            var panel = list.FirstOrDefault();

            foreach (var p in list.Where(p => p != panel))
            {
                p.DockTo(panel, state, size);
            }

            return true;
        }

        private DialogResult PromptSaveChanges(bool withCancel, string layerName)
        {
            var msg = "Save changes of the attribute table for the layer: " + layerName + "?";

            if (withCancel)
            {
                return MessageService.Current.AskWithCancel(msg);
            }

            return MessageService.Current.Ask(msg) ? DialogResult.Yes : DialogResult.No;
        }

        private void SaveLayoutOption(TableEditorCommand command)
        {
            switch (command)
            {
                case TableEditorCommand.LayoutTabbed:
                    AppConfig.Instance.TableEditorLayout = TableEditorLayout.Tabbed;
                    break;
                case TableEditorCommand.LayoutHorizontal:
                    AppConfig.Instance.TableEditorLayout = TableEditorLayout.Horizontal;
                    break;
                case TableEditorCommand.LayoutVertical:
                    AppConfig.Instance.TableEditorLayout = TableEditorLayout.Vertical;
                    break;
            }
        }

        private void ShowSelected()
        {
            var grid = View.ActiveGrid;
            if (grid == null)
            {
                return;
            }

            var manager = grid.RowManager;

            if (manager.Filtered)
            {
                manager.ClearFilter();
            }
            else
            {
                View.ClearCurrentCell();
                manager.FilterSelected(FeatureSet);
            }

            grid.RowCount = 0;
            grid.RowCount = manager.Count;
            grid.Invalidate();

            View.UpdateView();
        }

        #endregion
    }
}