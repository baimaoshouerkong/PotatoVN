﻿using System.Collections.ObjectModel;
using System.Reflection;
using Windows.Storage;
using Windows.Storage.Pickers;
using GalgameManager.Contracts.Phrase;
using GalgameManager.Contracts.Services;
using GalgameManager.Enums;
using GalgameManager.Helpers;
using GalgameManager.Helpers.Phrase;
using GalgameManager.Models;
using GalgameManager.Models.BgTasks;
using GalgameManager.Models.Sources;
using GalgameManager.Views.Dialog;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GalgameManager.Services;

public partial class GalgameCollectionService : IGalgameCollectionService
{
    // _galgames 无序, _displayGalgames有序
    private List<Galgame> _galgames = new();
    private readonly Dictionary<string, Galgame> _galgameMap = new(); // Url->Galgame
    private ObservableCollection<Galgame> _displayGalgames = new(); //用于显示的galgame列表
    private static ILocalSettingsService LocalSettingsService { get; set; } = null!;
    private readonly IJumpListService _jumpListService;
    private readonly IFilterService _filterService;
    private readonly IInfoService _infoService;
    private readonly IBgTaskService _bgTaskService;
    private readonly IGalgameSourceCollectionService _galSrcService;
    private string _searchKey = string.Empty;
    public event Action<Galgame>? GalgameAddedEvent; //当有galgame添加时触发
    public event Action<Galgame>? GalgameDeletedEvent; //当有galgame删除时触发
    public event Action<Galgame>? MetaSavedEvent; //当有galgame元数据保存时触发
    public event Action? GalgameLoadedEvent; //当galgame列表加载完成时触发
    public event Action? PhrasedEvent; //当有galgame信息下载完成时触发
    public event GenericDelegate<Galgame>? PhrasedEvent2; //当有galgame信息下载完成时触发 
    public bool IsPhrasing;

    public IGalInfoPhraser[] PhraserList
    {
        get;
    } = new IGalInfoPhraser[6];

    public GalgameCollectionService(ILocalSettingsService localSettingsService, IJumpListService jumpListService, 
        IGalgameSourceCollectionService galgameSourceService, IFilterService filterService, IInfoService infoService, 
        IBgTaskService bgTaskService)
    {
        LocalSettingsService = localSettingsService;
        LocalSettingsService.OnSettingChanged += async (key, _) => await OnSettingChanged(key);
        _jumpListService = jumpListService;
        _filterService = filterService;
        _filterService.OnFilterChanged += () => UpdateDisplay(UpdateType.ApplyFilter);
        _infoService = infoService;
        _bgTaskService = bgTaskService;
        _galSrcService = galgameSourceService;
        _galSrcService.OnSourceDeleted += HandleSourceDelete;
        
        BgmPhraser bgmPhraser = new(GetBgmData().Result);
        VndbPhraser vndbPhraser = new();
        YmgalPhraser ymgalPhraser = new();
        MixedPhraser mixedPhraser = new(bgmPhraser, vndbPhraser, ymgalPhraser, GetMixData());
        PhraserList[(int)RssType.Bangumi] = bgmPhraser;
        PhraserList[(int)RssType.Vndb] = vndbPhraser;
        PhraserList[(int)RssType.Ymgal] = ymgalPhraser;
        PhraserList[(int)RssType.Mixed] = mixedPhraser;
        
        SortKeys[] sortKeysList = LocalSettingsService.ReadSettingAsync<SortKeys[]>(KeyValues.SortKeys).Result ?? new[]
            { SortKeys.LastPlay , SortKeys.Developer};
        var sortKeysAscending = LocalSettingsService.ReadSettingAsync<bool[]>(KeyValues.SortKeysAscending).Result ?? new[]
            {false,false};
        Galgame.UpdateSortKeys(sortKeysList, sortKeysAscending);

        App.OnAppClosing += async () =>
        {
            await SaveGalgamesAsync();
        };
    }
    
    public async Task InitAsync()
    {
        await GetGalgames();
        await _jumpListService.CheckJumpListAsync(_galgames);
        await Upgrade();
    }

    public Task StartAsync()
    {
        UpdateDisplay(UpdateType.Init);
        return Task.CompletedTask;
    }

    private async Task GetGalgames()
    {
        _galgames = await LocalSettingsService.ReadSettingAsync<List<Galgame>>(KeyValues.Galgames, true) ?? new List<Galgame>();
        _galgames = _galgames.Where(g => g.SourceType is not GalgameSourceType.UnKnown).ToList();
        foreach (Galgame g in _galgames)
        {
            // if (g.SourceType is GalgameSourceType.LocalFolder or GalgameSourceType.LocalZip && !Path.Exists(g.Path))
            // {
            //     // g.Path = string.Empty;
            //     g.SourceType = GalgameSourceType.Virtual;
            // }
            _galgameMap[g.Url] = g;
            g.ErrorOccurred += e =>
                _infoService.Event(EventType.GalgameEvent, InfoBarSeverity.Warning, "GalgameEvent", e);
        }
        GalgameLoadedEvent?.Invoke();
    }

    /// <summary>
    /// 可能不同版本行为不同，需要对已存储的galgame进行升级
    /// </summary>
    private async Task Upgrade()
    {
        if (!await LocalSettingsService.ReadSettingAsync<bool>(KeyValues.IdFromMixedUpgraded))
        {
            foreach (Galgame galgame in _galgames)
                galgame.UpdateIdFromMixed();
            await LocalSettingsService.SaveSettingAsync(KeyValues.IdFromMixedUpgraded, true);
        }

        if (!await LocalSettingsService.ReadSettingAsync<bool>(KeyValues.SavePathUpgraded))
        {
            _galgames.ForEach(galgame => galgame.FindSaveInPath());
            await LocalSettingsService.SaveSettingAsync(KeyValues.SavePathUpgraded, true);
        }
        
        // 给混合搜刮器设置的搜刮优先级添加新添加的搜刮器
        if (await LocalSettingsService.ReadSettingAsync<int>(KeyValues.MixedPhraserOrderVersion) !=
            MixedPhraserOrder.Version)
            await MixedPhraserOrderUpdate();
    }

    /// <summary>
    /// 排序并更新显示的列表
    /// </summary>
    public void Sort()
    {
        UpdateDisplay(UpdateType.Sort);
    }
    
    public async Task RemoveGalgame(Galgame galgame, bool removeFromDisk = false)
    {
        _galgames.Remove(galgame);
        foreach (GalgameSourceBase s in galgame.Sources)
            _galSrcService.MoveOutOperate(s, galgame);
        if(galgame.CheckExistLocal())
            _galgameMap.Remove(galgame.Url);
        UpdateDisplay(UpdateType.Remove, galgame);
        if (removeFromDisk)
            galgame.Delete();
        GalgameDeletedEvent?.Invoke(galgame);
        await SaveGalgamesAsync();
    }

    public (Galgame, bool) TryRecoverGalgameFromLocal(Galgame galgame)
    {
        if (galgame.SourceType is not (GalgameSourceType.LocalFolder or GalgameSourceType.LocalZip)) return (galgame, false);
        var metaFolder = galgame.GetMetaPath();
        if (Path.Exists(Path.Combine(metaFolder, "meta.json")))
        {
            try
            {
                galgame = Galgame.ResolveMetaFromLocalFolder(galgame, metaFolder);
            }
            catch (Exception) // 文件不合法
            {
                throw new Exception("GalgameCollectionService_PhraseFileFailed".GetLocalized());
            }
            PhrasedEvent?.Invoke();
        }
        return (galgame, true);
    }
    
    public Galgame? GetVirtualGame(Galgame galgame)=>_galgames.FirstOrDefault(g =>
        string.IsNullOrEmpty(g.Ids[(int)RssType.Bangumi]) == false
        && g.Ids[(int)RssType.Bangumi] == galgame.Ids[(int)RssType.Bangumi]
        && g.CheckExistLocal() == false);

    /// <summary>
    /// 试图添加一个galgame，若已存在则不添加
    /// </summary>
    /// <param name="galgame"></param>
    /// <param name="isForce">是否强制添加（若RSS源中找不到相关游戏信息）</param>
    /// <param name="virtualGame">如果是要给虚拟游戏设置本地路径，则填入对应的虚拟游戏</param>
    public async Task<AddGalgameResult> TryAddGalgameAsync(Galgame galgame , bool isForce = false, Galgame? virtualGame = null)
    {
        if (_galgames.Any(gal => gal.Path == galgame.Path && gal.SourceType == galgame.SourceType))
            return AddGalgameResult.AlreadyExists;

        
        // 已存在虚拟游戏则将虚拟游戏变为真实游戏（设置Path）
        virtualGame ??= GetVirtualGame(galgame);
        if (virtualGame is not null)
        {
            virtualGame.Path = galgame.Path;
            virtualGame.SourceType = galgame.SourceType;
            galgame = virtualGame;
        } else if (virtualGame is null)
        {
            // 覆盖sourceType
            GalgameSourceType sourceType = galgame.SourceType;
            bool recoverSuccess;
            (galgame, recoverSuccess) = TryRecoverGalgameFromLocal(galgame);
            galgame.SourceType = sourceType;
            if (!recoverSuccess)
            {
                var pattern = await LocalSettingsService.ReadSettingAsync<string>(KeyValues.RegexPattern) ?? ".+";
                var regexIndex = await LocalSettingsService.ReadSettingAsync<int>(KeyValues.RegexIndex);
                var removeBorder = await LocalSettingsService.ReadSettingAsync<bool>(KeyValues.RegexRemoveBorder);
                galgame.Name.Value = NameRegex.GetName(galgame.Name!, pattern, removeBorder, regexIndex);
                if (string.IsNullOrEmpty(galgame.Name)) return AddGalgameResult.NotFoundInRss;
                galgame = await PhraseGalInfoAsync(galgame);
                if (!isForce && galgame.RssType == RssType.None)
                    return AddGalgameResult.NotFoundInRss;
            }
        }
        
        galgame.FindSaveInPath();
        if(virtualGame is null)
            _galgames.Add(galgame);
        _galgameMap[galgame.Url] = galgame;
        GalgameAddedEvent?.Invoke(galgame);
        await SaveGalgamesAsync(galgame);
        UpdateDisplay(virtualGame is null ? UpdateType.Add : UpdateType.Update, galgame);
        galgame.ErrorOccurred += e =>
            _infoService.Event(EventType.GalgameEvent, InfoBarSeverity.Warning, "GalgameEvent", e);
        return galgame.RssType == RssType.None ? AddGalgameResult.NotFoundInRss : AddGalgameResult.Success;
    }

    public void AddVirtualGalgame(Galgame game)
    {
        _galgames.Add(game);
        GalgameAddedEvent?.Invoke(game);
        UpdateDisplay(UpdateType.Add, game);
    }

    /// <summary>
    /// 从下载源获取这个galgame的信息，并获取游玩状态（若设置里开启）
    /// </summary>
    /// <param name="galgame">galgame</param>
    /// <param name="rssType">信息源，若设置为None则使用galgame指定的数据源，若不存在则使用设置中的默认数据源</param>
    /// <returns>获取信息后的galgame，如果信息源不可达则galgame保持不变</returns>
    public async Task<Galgame> PhraseGalInfoAsync(Galgame galgame, RssType rssType = RssType.None)
    {
        IsPhrasing = true;
        RssType selectedRss = rssType;
        if(selectedRss == RssType.None)
            selectedRss = galgame.RssType == RssType.None ? await LocalSettingsService.ReadSettingAsync<RssType>(KeyValues.RssType) : galgame.RssType;
        Galgame result = await PhraserAsync(galgame, PhraserList[(int)selectedRss]);
        if (await LocalSettingsService.ReadSettingAsync<bool>(KeyValues.SyncPlayStatusWhenPhrasing))
            await DownLoadPlayStatusAsync(galgame, RssType.Bangumi);
        await LocalSettingsService.SaveSettingAsync(KeyValues.Galgames, _galgames, true);
        IsPhrasing = false;
        PhrasedEvent?.Invoke();
        PhrasedEvent2?.Invoke(galgame);
        _ = _bgTaskService.AddBgTask(new GetGalgameCharactersFromRssTask(galgame));
        return result;
    }
    public async Task<GalgameCharacter> PhraseGalCharacterAsync(GalgameCharacter galgameCharacter, RssType rssType = RssType.None)
    {
        GalgameCharacter result = await PhraserCharacterAsync(galgameCharacter, PhraserList[(int)rssType]);
        return result;
    }

    private static async Task<GalgameCharacter> PhraserCharacterAsync(GalgameCharacter galgameCharacter, IGalInfoPhraser phraser)
    {
        if (phraser is not IGalCharacterPhraser characterPhraser) return galgameCharacter;
        GalgameCharacter? tmp = await characterPhraser.GetGalgameCharacter(galgameCharacter);
        if (tmp == null) return galgameCharacter;
        galgameCharacter.Name = tmp.Name;
        galgameCharacter.Summary = tmp.Summary;
        galgameCharacter.Gender = tmp.Gender;
        galgameCharacter.BirthDay = tmp.BirthDay;
        galgameCharacter.BirthMon = tmp.BirthMon;
        galgameCharacter.BirthYear = tmp.BirthYear;
        galgameCharacter.BirthDate = tmp.BirthDate;
        galgameCharacter.BloodType = tmp.BloodType;
        galgameCharacter.Height = tmp.Height;
        galgameCharacter.Weight = tmp.Weight;
        galgameCharacter.BWH = tmp.BWH;
        
        galgameCharacter.ImagePath = await DownloadHelper.DownloadAndSaveImageAsync(tmp.ImageUrl, 
            fileNameWithoutExtension:$"{galgameCharacter.Name}_Large") ?? Galgame.DefaultImagePath;
        galgameCharacter.PreviewImagePath = await DownloadHelper.DownloadAndSaveImageAsync(tmp.PreviewImageUrl, 
                                                fileNameWithoutExtension:$"{galgameCharacter.Name}_Preview") ??
                                            Galgame.DefaultImagePath;
        return galgameCharacter;
    }

    private static async Task<Galgame> PhraserAsync(Galgame galgame, IGalInfoPhraser phraser)
    {
        Galgame? tmp = await phraser.GetGalgameInfo(galgame);
        if (tmp == null) return galgame;

        galgame.RssType = phraser.GetPhraseType();
        galgame.Id = tmp.Id;
        if (phraser is MixedPhraser)
            galgame.UpdateIdFromMixed();
        galgame.Description.Value = tmp.Description.Value;
        if (tmp.Developer != Galgame.DefaultString)
            galgame.Description.Value = tmp.Description.Value;
        if (tmp.Developer != Galgame.DefaultString)
            galgame.Developer.Value = tmp.Developer.Value;
        if (tmp.ExpectedPlayTime != Galgame.DefaultString)
            galgame.ExpectedPlayTime.Value = tmp.ExpectedPlayTime.Value;
        if (await LocalSettingsService.ReadSettingAsync<bool>(KeyValues.OverrideLocalName))
        {
            if (await LocalSettingsService.ReadSettingAsync<bool>(KeyValues.OverrideLocalNameWithChinese))
            {
                galgame.Name.Value = !string.IsNullOrEmpty(tmp.CnName) ? tmp.CnName : tmp.Name.Value;
            }
            else
            {
                galgame.Name.Value = tmp.Name.Value;
            }
        }
        galgame.ImageUrl = tmp.ImageUrl;
        galgame.Rating.Value = tmp.Rating.Value;
        galgame.Tags.Value = tmp.Tags.Value;
        galgame.Characters = tmp.Characters;
        galgame.ImagePath.Value = await DownloadHelper.DownloadAndSaveImageAsync(galgame.ImageUrl) ?? Galgame.DefaultImagePath;
        galgame.ReleaseDate = tmp.ReleaseDate.Value;
        return galgame;
    }
    
    /// <summary>
    /// 下载某个游戏的游玩状态
    /// </summary>
    /// <param name="galgame">游戏</param>
    /// <param name="source">下载源</param>
    /// <returns>(下载结果，结果解释)</returns>
    public async Task<(GalStatusSyncResult, string)> DownLoadPlayStatusAsync(Galgame galgame, RssType source)
    {
        if (source == RssType.Bangumi && PhraserList[(int)RssType.Bangumi] is BgmPhraser bgmPhraser)
            return await bgmPhraser.DownloadAsync(galgame);
        return (GalStatusSyncResult.Other, "这个数据源不支持同步游玩状态");
    }

    /// <summary>
    /// 从某个信息源下载所有游戏的游玩状态
    /// </summary>
    /// <param name="source">信息源</param>
    /// <returns>(结果，结果解释)</returns>
    public async Task<(GalStatusSyncResult ,string)> DownloadAllPlayStatus(RssType source)
    {
        var msg = string.Empty;
        GalStatusSyncResult result = GalStatusSyncResult.Other;
        IGalInfoPhraser phraser = PhraserList[(int)source];
        if (phraser is IGalStatusSync sync)
            (result, msg) = await sync.DownloadAllAsync(_galgames);
        return (result, msg);
    }

    /// <summary>
    /// 获取要显示的galgame列表
    /// </summary>
    public async Task<ObservableCollection<Galgame>> GetGalgameSourcesAsync()
    {
        await Task.CompletedTask;
        return _displayGalgames;
    }

    /// <summary>
    /// 刷新显示列表
    /// </summary>
    public void RefreshDisplay()
    {
        UpdateDisplay(UpdateType.Init);
    }

    /// <summary>
    /// 向信息源上传游玩状态
    /// </summary>
    /// <param name="galgame">要同步的游戏</param>
    /// <param name="rssType">信息源</param>
    /// <returns>(上传结果， 结果解释)</returns>
    /// <exception cref="NotSupportedException">若信息源没有实现IGalStatusSync，则抛此异常</exception>
    public async Task<(GalStatusSyncResult, string)> UploadPlayStatusAsync(Galgame galgame, RssType rssType)
    {
        IGalInfoPhraser phraser = PhraserList[(int)rssType];
        if (phraser is IGalStatusSync syncer)
            return await syncer.UploadAsync(galgame);
        throw new NotSupportedException("这个数据源不支持同步游玩状态");
    }

    /// <summary>
    /// 搜索galgame并更新显示列表
    /// </summary>
    /// <param name="searchKey">搜索关键字</param>
    public void Search(string searchKey)
    {
        _searchKey = searchKey;
        UpdateDisplay(UpdateType.ApplySearch);
    }

    /// <summary>
    /// 获取搜索建议
    /// </summary>
    /// <param name="current">当前文本串</param>
    /// <returns>搜索建议，若没有则返回空List</returns>
    public async Task<List<string>> GetSearchSuggestions(string current)
    {
        List<string> tmp = new();
        await Task.Run(() =>
        {
            //Name
            tmp.AddRange(from galgame in _galgames
                where galgame.Name.Value is not null && galgame.Name.Value.ContainX(current) select galgame.Name.Value);
            //Developer
            tmp.AddRange(from galgame in _galgames
                where galgame.Developer.Value is not null && galgame.Developer.Value.ContainX(current)
                select galgame.Developer.Value);
            //Tag
            tmp.AddRange(from galgame in _galgames
                from tag in galgame.Tags.Value ?? new ObservableCollection<string>()
                where tag.ContainX(current)
                select tag);
        });
        //去重
        tmp.Sort((a,b)=> a.CompareX(b));
        return tmp.Where((t, i) => i == 0 || t.CompareX(tmp[i - 1]) !=0).ToList();
    }

    /// <summary>
    /// 获取搜索关键字(的clone)
    /// </summary>
    public string GetSearchKey()
    {
        return (string)_searchKey.Clone();
    }
    
    public List<Galgame> Galgames => _galgames;

    /// <summary>
    /// 从Url获取galgame
    /// </summary>
    /// <param name="url">url</param>
    /// <returns>galgame,若找不到则返回null</returns>
    public Galgame? GetGalgameFromUrl(string url)
    {
        return string.IsNullOrEmpty(url) ? null : _galgameMap.GetValueOrDefault(url);
    }
    
    public Galgame? GetGalgameFromUid(GalgameUid uid)
    {
        var max = 0;
        Galgame? result = null;
        foreach(Galgame g in _galgames)
            if (g.Uid.Similarity(uid) > max)
            {
                result = g;
                max = g.Uid.Similarity(uid);
            }
        return result;
    }
    
    public Galgame? GetGalgameFromId(string? id, RssType rssType)
    {
        if (id is null) return null;
        return _galgames.FirstOrDefault(g => g.Ids[(int)rssType] == id);
    }
    
    public Galgame? GetGalgameFromName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return _galgames.Find(g => g.Name.Value == name);
    }
    
    /// <summary>
    /// 保存galgame列表（以及其内部的galgame）
    /// </summary>
    /// <param name="galgame">
    /// 要指定保存的galgame，若为null则不做文件夹保存，只做缓存保存 <br/>
    /// 如果设置中没有打开保存备份则不会保存到游戏文件夹
    /// </param>
    public async Task SaveGalgamesAsync(Galgame? galgame = null)
    {
        await LocalSettingsService.SaveSettingAsync(KeyValues.Galgames, _galgames, true);
        if (galgame is not null && await LocalSettingsService.ReadSettingAsync<bool>(KeyValues.SaveBackupMetadata))
            await SaveMetaAsync(galgame);
    }
    
    /// <summary>
    /// 保存galgame的信息备份（包括meta.json和封面图）
    /// </summary>
    /// <param name="galgame"></param>
    private async Task SaveMetaAsync(Galgame galgame)
    {
        IEnumerable<GalgameSourceType> types = galgame.Sources.Select(s => s.SourceType).Distinct();
        List<(Task, GalgameSourceType)> tasks = new();
        foreach (GalgameSourceType type in types) 
            tasks.Add((SourceServiceFactory.GetSourceService(type).SaveMetaAsync(galgame), type));
        foreach ((Task, GalgameSourceType) t in tasks)
        {
            try
            {
                await t.Item1;
            }
            catch (Exception e)
            {
                _infoService.Event(EventType.GalgameEvent, InfoBarSeverity.Warning,
                    "GalgameCollectionService_BackupMetaFailed".GetLocalized(galgame.Name.Value
                                                                             ?? string.Empty, t.Item2.ToString()), e);
            }
        }
    }

    /// <summary>
    /// 保存所有galgame的信息备份（包括meta.json和封面图）
    /// </summary>
    public async Task SaveAllMetaAsync()
    {
        foreach (Galgame galgame in _galgames)
        {
            MetaSavedEvent?.Invoke(galgame);
            await SaveMetaAsync(galgame);
        }
    }

    /// <summary>
    /// 获取galgame的存档文件夹
    /// </summary>
    /// <param name="galgame">galgame</param>
    /// <returns>存档文件夹地址，若用户取消返回null</returns>
    private async Task<string?> GetGalgameSaveAsync(Galgame galgame)
    {
        List<string> subFolders = galgame.GetSubFolders();
        FolderPickerDialog dialog = new(App.MainWindow!.Content.XamlRoot, "GalgameCollectionService_SelectSavePosition".GetLocalized(), subFolders);
        return await dialog.ShowAndAwaitResultAsync();
    }
    
    /// <summary>
    /// 获取并设置galgame的可执行文件
    /// </summary>
    /// <param name="galgame">galgame</param>
    /// <returns>可执行文件地址，如果用户取消或找不到可执行文件则返回null</returns>
    public async Task<string?> GetGalgameExeAsync(Galgame galgame)
    {
        if (!galgame.CheckExistLocal()) return null;
        List<string> exes = galgame.GetExesAndBats();
        switch (exes.Count)
        {
            case 0:
            {
                ContentDialog dialog = new()
                {
                    XamlRoot = App.MainWindow!.Content.XamlRoot,
                    Title = "Error".GetLocalized(),
                    Content = "GalgameCollectionService_NotExeFounded".GetLocalized(),
                    PrimaryButtonText = "Yes".GetLocalized()
                };
                await dialog.ShowAsync();
                return null;
            }
            case 1:
                galgame.ExePath = exes[0];
                break;
            default:
            {
                SelectFileDialog dialog = new(galgame.Path, new[] {".exe", ".bat", ".lnk"}, 
                    "GalgameCollectionService_SelectExe".GetLocalized(), false);
                await dialog.ShowAsync();
                if (dialog.SelectedFilePath == null) return null;
                galgame.ExePath = dialog.SelectedFilePath;
                break;
            }
        }
        return galgame.ExePath;
    }

    /// <summary>
    /// 转换存档位置
    /// </summary>
    /// <param name="galgame">galgame</param>
    public async Task ChangeGalgameSavePosition(Galgame galgame)
    {
        if (galgame.SavePath is not null && new DirectoryInfo(galgame.SavePath).Exists == false)
            galgame.SavePath = null;
            
        if (galgame.SavePath is not null) //目前在云端
        {
            await Task.Run(() =>
            {
                FolderOperations.ConvertSymbolicLinkToActual(galgame.SavePath);
                galgame.SavePath = null;
            });
        }
        else //目前在本地
        {
            var remoteRoot = await LocalSettingsService.ReadSettingAsync<string>(KeyValues.RemoteFolder);
            if (string.IsNullOrEmpty(remoteRoot))
            {
                ContentDialog dialog = new()
                {
                    XamlRoot = App.MainWindow!.Content.XamlRoot,
                    Title = "Error".GetLocalized(),
                    Content = "GalgameCollectionService_CloudRootNotSet".GetLocalized(),
                    PrimaryButtonText = "Yes".GetLocalized()
                };
                await dialog.ShowAsync();
                return;
            }
            var localSavePath = await GetGalgameSaveAsync(galgame);
            if (localSavePath == null) return;
            var tmp = localSavePath[..localSavePath.LastIndexOf('\\')];
            var target = tmp[tmp.LastIndexOf('\\')..] + localSavePath[localSavePath.LastIndexOf('\\')..];
            remoteRoot += target;

            try
            {
                if (new DirectoryInfo(remoteRoot).Exists) //云端已存在同名文件夹
                {
                    var choose = 0;
                    ContentDialog dialog = new()
                    {
                        XamlRoot = App.MainWindow!.Content.XamlRoot,
                        Title = "GalgameCollectionService_SelectOperateTitle".GetLocalized(),
                        Content = "GalgameCollectionService_SelectOperateMsg".GetLocalized(),
                        PrimaryButtonText = "GalgameCollectionService_Local".GetLocalized(),
                        SecondaryButtonText = "GalgameCollectionService_Cloud".GetLocalized(),
                        CloseButtonText = "Cancel".GetLocalized()
                    };
                    dialog.PrimaryButtonClick += (_, _) => choose = 1;
                    dialog.SecondaryButtonClick += (_, _) => choose = 2;
                    await dialog.ShowAsync();
                    if (choose == 1)
                    {
                        new DirectoryInfo(remoteRoot).Delete(true); //删除云端文件夹
                        FolderOperations.ConvertFolderToSymbolicLink(localSavePath, remoteRoot);
                    }
                    else if (choose == 2)
                    {
                        new DirectoryInfo(localSavePath).Delete(true); //删除本地文件夹
                        FolderOperations.CreateSymbolicLink(localSavePath, remoteRoot);
                    }
                }
                else
                    FolderOperations.ConvertFolderToSymbolicLink(localSavePath, remoteRoot);
                galgame.SavePath = localSavePath;
            }
            catch (Exception e) //创建符号链接失败，把存档复制回去
            {
                if(Directory.Exists(localSavePath))
                    Directory.Delete(localSavePath, true);
                FolderOperations.Copy(remoteRoot, localSavePath);
                //弹出提示框
                StackPanel stackPanel = new();
                stackPanel.Children.Add(new TextBlock {Text = "GalgameCollectionService_CreateSymbolicLinkFailed".GetLocalized()});
                stackPanel.Children.Add(new TextBlock
                {
                    Text = e.Message + "\n" + e.StackTrace, 
                    TextWrapping = TextWrapping.Wrap
                });
                ContentDialog dialog = new()
                {
                    XamlRoot = App.MainWindow!.Content.XamlRoot,
                    Title = "Error".GetLocalized(),
                    Content = stackPanel,
                    PrimaryButtonText = "Yes".GetLocalized()
                };
                await dialog.ShowAsync();
            }
        }
        
        await SaveGalgamesAsync(galgame);
    }

    /// <summary>
    /// 获取并设置galgame排序的关键字
    /// </summary>
    public async Task SetSortKeysAsync()
    {
        List<SortKeys> sortKeysList = new()
        {
            SortKeys.Name,
            SortKeys.Developer,
            SortKeys.Rating,
            SortKeys.LastPlay,
            SortKeys.ReleaseDate
        };
        ContentDialog dialog = new()
        {
            XamlRoot = App.MainWindow!.Content.XamlRoot,
            Title = "排序",
            PrimaryButtonText = "Yes".GetLocalized(),
            SecondaryButtonText = "Cancel".GetLocalized(),
        };
        
        ComboBox comboBox1 = new()
        {
            Header = "第一关键字",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = sortKeysList,
            Margin = new Thickness(0, 0, 5, 0),
            SelectedItem = Galgame.SortKeysList[0]
        };
        ToggleSwitch toggleSwitch1 = new()
        {
            Header = "降序/升序",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(5, 0, 0, 0),
            OnContent = "升序",
            OffContent = "降序",
            IsOn = Galgame.SortKeysAscending[0]
        };
        StackPanel panel1 = new ();
        panel1.Children.Add(comboBox1);
        panel1.Children.Add(toggleSwitch1);
        Grid.SetColumn(panel1, 0 );
        
        ComboBox comboBox2 = new()
        {
            Header = "第二关键字",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = sortKeysList,
            Margin = new Thickness(0, 0, 5, 0),
            SelectedItem = Galgame.SortKeysList[1]
        };
        ToggleSwitch toggleSwitch2 = new()
        {
            Header = "降序/升序",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(5, 0, 0, 0),
            OnContent = "升序",
            OffContent = "降序",
            IsOn = Galgame.SortKeysAscending[1]
        };
        StackPanel panel2 = new ();
        panel2.Children.Add(comboBox2);
        panel2.Children.Add(toggleSwitch2);
        Grid.SetColumn(panel2, 1 );
        

        dialog.PrimaryButtonClick += async (_, _) =>
        {
            Galgame.UpdateSortKeys(
                new[] { (SortKeys)comboBox1.SelectedItem, (SortKeys)comboBox2.SelectedItem },
                new []{toggleSwitch1.IsOn, toggleSwitch2.IsOn});
            await LocalSettingsService.SaveSettingAsync(KeyValues.SortKeys, Galgame.SortKeysList);
            await LocalSettingsService.SaveSettingAsync(KeyValues.SortKeysAscending, Galgame.SortKeysAscending);
            Sort();
        };
        Grid content = new();
        content.ColumnDefinitions.Add(new ColumnDefinition{Width = new GridLength(1, GridUnitType.Star)});
        content.ColumnDefinitions.Add(new ColumnDefinition{Width = new GridLength(1, GridUnitType.Star)});
        content.Children.Add(panel1);
        content.Children.Add(panel2);
        dialog.Content = content;
        await dialog.ShowAsync();
    }

    /// <summary>
    /// 从设置中读取bangumi的设置
    /// </summary>
    private async Task<BgmPhraserData> GetBgmData()
    {
        BgmPhraserData data = new()
        {
            Token = (await LocalSettingsService.ReadSettingAsync<BgmAccount>(KeyValues.BangumiAccount))?.BangumiAccessToken ?? ""
        };
        return data;
    }

    private MixedPhraserData GetMixData()
    {
        return new MixedPhraserData
        {
            Order = LocalSettingsService.ReadSettingAsync<MixedPhraserOrder>(KeyValues.MixedPhraserOrder).Result!,
        };
    }

    private async Task OnSettingChanged(string key)
    {
        switch (key)
        {
            case KeyValues.BangumiAccount:
                PhraserList[(int)RssType.Bangumi].UpdateData(await GetBgmData());
                break;
            case KeyValues.MixedPhraserOrder:
                PhraserList[(int)RssType.Mixed].UpdateData(GetMixData());
                break;
            case KeyValues.SortKeys:
                Galgame.UpdateSortKeys(await LocalSettingsService.ReadSettingAsync<SortKeys[]>(KeyValues.SortKeys) ?? new[]
                {
                    SortKeys.Name,
                    SortKeys.Rating
                });
                break;
            case KeyValues.SortKeysAscending:
                Galgame.UpdateSortKeysAscending(await LocalSettingsService.ReadSettingAsync<bool[]>(KeyValues.SortKeysAscending) ?? new[]
                {
                    false,
                    false
                });
                break;
        }
    }
    
    public async Task ToLocalGalgame(Galgame galgame)
    {
        if (galgame.SourceType != GalgameSourceType.LocalZip) return;
        UnpackDialog dialog = new();
        await dialog.ShowAsync(await StorageFile.GetFileFromPathAsync(galgame.Path), true);
        StorageFile? file = dialog.StorageFile;
        GalgameSourceBase? source = dialog.Source;
        if (file == null || source is not GalgameFolderSource folderSource) return;
        _ = _bgTaskService.AddBgTask(new UnpackGameTask(file, folderSource, dialog.GameName, dialog.Password));
    }

    private async void HandleSourceDelete(GalgameSourceBase source)
    {
        try
        {
            foreach (Galgame galgame in source.GetGalgameList())
            {
                _galSrcService.MoveOutOperate(source, galgame);
                if(galgame.Sources.Count == 0)
                    await RemoveGalgame(galgame);
            }
        }
        catch (Exception e)
        {
            _infoService.DeveloperEvent(InfoBarSeverity.Error, e: e);
        }
    }

    private async Task MixedPhraserOrderUpdate()
    {
        try
        {
            MixedPhraserOrder orders =
                (await LocalSettingsService.ReadSettingAsync<MixedPhraserOrder>(KeyValues.MixedPhraserOrder))!;
            IEnumerable<PropertyInfo> properties = orders.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(ObservableCollection<RssType>));
            MixedPhraserOrder defOrder = new MixedPhraserOrder().SetToDefault();
            foreach (PropertyInfo prop in properties)
            {
                ObservableCollection<RssType> order = (ObservableCollection<RssType>)prop.GetValue(orders)!;
                ObservableCollection<RssType> target = (ObservableCollection<RssType>)prop.GetValue(defOrder)!;
                foreach (RssType type in target.Where(type => !order.Contains(type)))
                    order.Add(type);
            }

            await LocalSettingsService.SaveSettingAsync(KeyValues.MixedPhraserOrderVersion,
                MixedPhraserOrder.Version);
            await LocalSettingsService.SaveSettingAsync(KeyValues.MixedPhraserOrder, orders);
        }
        catch (Exception e) //不应该发生
        {
            _infoService.Event(EventType.AppError, InfoBarSeverity.Error, "Upgrade failed", e);
        }
    }
}

public class FolderPickerDialog : ContentDialog
{
    private string? _selectedFolder;
    private readonly TaskCompletionSource<string?> _folderSelectedTcs = new TaskCompletionSource<string?>();
    public FolderPickerDialog(XamlRoot xamlRoot, string title, List<string> files)
    {
        XamlRoot = xamlRoot;
        Title = title;
        Content = CreateContent(files);
        PrimaryButtonText = "Yes".GetLocalized();
        SecondaryButtonText = "GalgameCollectionService_FolderPickerDialog_ChoseAnotherFolder".GetLocalized();
        CloseButtonText = "Cancel".GetLocalized();
        IsPrimaryButtonEnabled = false;
        PrimaryButtonClick += (_, _) => { _folderSelectedTcs.TrySetResult(_selectedFolder); };
        SecondaryButtonClick += async (_, _) =>
        {
            FolderPicker folderPicker = new();
            folderPicker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, App.MainWindow!.GetWindowHandle());
            StorageFolder? folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null)
            {
                _selectedFolder = folder.Path;
                _folderSelectedTcs.TrySetResult(folder.Path);
            }
            else
                _folderSelectedTcs.TrySetResult(null);
        };
        CloseButtonClick += (_, _) => { _folderSelectedTcs.TrySetResult(null); };
    }
    private UIElement CreateContent(List<string> files)
    {
        StackPanel stackPanel = new();
        foreach (var file in files)
        {
            RadioButton radioButton = new()
            {
                Content = file,
                GroupName = "ExeFiles"
            };
            radioButton.Checked += RadioButton_Checked;
            stackPanel.Children.Add(radioButton);
        }
        return stackPanel;
    }
    private void RadioButton_Checked(object sender, RoutedEventArgs e)
    {
        RadioButton radioButton = (RadioButton)sender;
        _selectedFolder = radioButton.Content.ToString()!;
        IsPrimaryButtonEnabled = true;
    }
    public async Task<string?> ShowAndAwaitResultAsync()
    {
        await ShowAsync();
        return await _folderSelectedTcs.Task;
    }
}
