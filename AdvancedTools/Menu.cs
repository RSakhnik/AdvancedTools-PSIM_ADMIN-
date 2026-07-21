using AdvancedTools.Pages;
using Ascon.Pilot.Bim.SDK;
using Ascon.Pilot.Bim.SDK.ModelStorage;
using Ascon.Pilot.SDK;
using Ascon.Pilot.SDK.Menu;
using Ascon.Pilot.SDK.Toolbar;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using PilotDataObject = Ascon.Pilot.SDK.IDataObject;

namespace AdvancedTools
{
    [Export(typeof(IMenu<MainViewContext>))]
    [Export(typeof(IMenu<ObjectsViewContext>))]
    [Export(typeof(IToolbar<ObjectsViewContext>))]
    public class Menu : IMenu<MainViewContext>, IMenu<ObjectsViewContext>, IToolbar<ObjectsViewContext>
    {
        private const string FilesSizeReportSelectedCommand = "Files_Size_Report_Selected";
        private const string FilesSizeReportToolbarCommand = "Files_Size_Report_Toolbar";

        private readonly IPilotServiceProvider _pilotServiceProvider;
        private readonly IObjectsRepository _objectsRepository;
        private readonly ISearchService _searchService;
        private readonly ITabServiceProvider _tabServiceProvider;

        private IModelStorageProvider _modelManager;

        private IPerson _specificPerson;


        // Pages
        MoverWindow _moverWindow;
        BDLoaderWindow _bdLoaderWindow;
        BDIntexaWindow _bdIntexaWindow;
        BDdevLoaderWindow _bddevLoaderWindow;
        FilesSizeReportWindow _filesSizeReportWindow;

        public void Build(IMenuBuilder builder, MainViewContext context)
        {
            var item = builder.AddItem("AdvancedTools", 1).WithHeader("ГПН.Утилиты.Админ");

            item.WithSubmenu().AddItem("Mover", 0).WithHeader("Координация IFC");
            item.WithSubmenu().AddItem("DB_ACCE_Loader", 1).WithHeader("БД АССЕ");
            item.WithSubmenu().AddItem("DB_devACCE_Loader", 2).WithHeader("БД devАССЕ");
            item.WithSubmenu().AddItem("DB_INTEXA_Loader", 3).WithHeader("БД Интекса");
        }

        public void Build(IMenuBuilder builder, ObjectsViewContext context)
        {
            var selectedObjects = context.SelectedObjects == null
                ? new List<PilotDataObject>()
                : context.SelectedObjects.ToList();

            if (selectedObjects.Count == 0)
                return;

            var item = builder.AddItem("AdvancedTools_Selected", builder.Count).WithHeader("ГПН.Утилиты.Админ");
            item.WithSubmenu().AddItem(FilesSizeReportSelectedCommand, 0).WithHeader("Размер файлов БД от выбранного объекта");
        }

        public void Build(IToolbarBuilder builder, ObjectsViewContext context)
        {
            var selectedObjects = GetSelectedObjects(context);
            var index = builder.Count;

            if (builder.ItemNames.LastOrDefault() != "tbiSeparator")
            {
                builder.AddSeparator(index);
                index++;
            }

            builder.AddButtonItem(FilesSizeReportToolbarCommand, index)
                .WithHeader("Размер файлов БД")
                .WithIsEnabled(selectedObjects.Count > 0)
                .WithHint("Построить отчет по размерам файлов от выбранного объекта");
        }

        public void OnMenuItemClick(string name, MainViewContext context)
        {
            if (name == "Mover")
            {

                _moverWindow = new MoverWindow();
                _moverWindow.Show();

            }

            else if (name == "DB_ACCE_Loader")
            {
                _bdLoaderWindow = new BDLoaderWindow();
                _bdLoaderWindow.Show();
            }

            else if (name == "DB_INTEXA_Loader")
            {
                _bdIntexaWindow = new BDIntexaWindow();
                _bdIntexaWindow.Show();
            }
            else if (name == "DB_devACCE_Loader")
            {
                _bddevLoaderWindow = new BDdevLoaderWindow();
                _bddevLoaderWindow.Show();
            }
        }

        public void OnMenuItemClick(string name, ObjectsViewContext context)
        {
            if (name != FilesSizeReportSelectedCommand)
                return;

            ShowFilesSizeReportForSelectedObjects(context);
        }

        public void OnToolbarItemClick(string name, ObjectsViewContext context)
        {
            if (name != FilesSizeReportToolbarCommand)
                return;

            ShowFilesSizeReportForSelectedObjects(context);
        }

        private void ShowFilesSizeReportForSelectedObjects(ObjectsViewContext context)
        {
            var selectedObjects = GetSelectedObjects(context);

            if (selectedObjects.Count == 0)
            {
                MessageBox.Show("Выберите объект, папку или проект в дереве документов Pilot.", "Размер файлов БД",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _filesSizeReportWindow = new FilesSizeReportWindow(_objectsRepository, selectedObjects);
            _filesSizeReportWindow.Show();
        }

        private static List<PilotDataObject> GetSelectedObjects(ObjectsViewContext context)
        {
            return context == null || context.SelectedObjects == null
                ? new List<PilotDataObject>()
                : context.SelectedObjects.ToList();
        }


        [ImportingConstructor]
        public Menu(IPilotServiceProvider pilotServiceProvider, IObjectsRepository objectsRepository, ISearchService searchService, ITabServiceProvider tabServiceProvider)
        {
            _pilotServiceProvider = pilotServiceProvider;
            _objectsRepository = objectsRepository;
            _tabServiceProvider = tabServiceProvider;
            _searchService = searchService;

            _modelManager = _pilotServiceProvider.GetServices<IModelStorageProvider>().FirstOrDefault();

            _specificPerson = _objectsRepository.GetCurrentPerson();
        }



    }
}
