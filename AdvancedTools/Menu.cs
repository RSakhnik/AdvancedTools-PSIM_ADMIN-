using AdvancedTools.Pages;
using Ascon.Pilot.Bim.SDK;
using Ascon.Pilot.Bim.SDK.ModelStorage;
using Ascon.Pilot.SDK;
using Ascon.Pilot.SDK.Menu;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;

namespace AdvancedTools
{
    [Export(typeof(IMenu<MainViewContext>))]
    public class Menu : IMenu<MainViewContext>
    {
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

        public void Build(IMenuBuilder builder, MainViewContext context)
        {
            var item = builder.AddItem("AdvancedTools", 1).WithHeader("ГПН.Утилиты.Админ");

            item.WithSubmenu().AddItem("Mover", 0).WithHeader("Координация IFC");
            item.WithSubmenu().AddItem("DB_ACCE_Loader", 1).WithHeader("БД АССЕ");
            item.WithSubmenu().AddItem("DB_devACCE_Loader", 2).WithHeader("БД devАССЕ");
            item.WithSubmenu().AddItem("DB_INTEXA_Loader", 3).WithHeader("БД Интекса");


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
