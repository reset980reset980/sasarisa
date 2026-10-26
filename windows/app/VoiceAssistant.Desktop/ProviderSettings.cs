using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VoiceAssistant.Desktop;

public partial class MainWindow
{
    bool _loadingProviders;
    string _draftProvider = "claude";
    Dictionary<string, ProviderPreferences> _providerProfiles = new();
    CancellationTokenSource? _apiRequest;
    CancellationTokenSource? _asideRequest;

    void StoreProviderDraft() => _providerProfiles[_draftProvider] = new(ModelInput.Text.Trim(), BrainUrlInput.Text.Trim());
    void ProviderInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged fires before the remaining XAML fields are constructed.
        if (ApiFields == null || _loadingProviders) return;
        _apiRequest?.Cancel();
        _asideRequest?.Cancel();
        StoreProviderDraft();
        _draftProvider = SelectedTag(ProviderInput);
        var profile = _providerProfiles.GetValueOrDefault(_draftProvider) ?? new ProviderPreferences("", ApiProviders.DefaultUrl(_draftProvider));
        _loadingProviders = true;
        ModelInput.Text = profile.Model;
        BrainUrlInput.Text = ApiProviders.Find(_draftProvider) is { EditableUrl: false } api ? api.BaseUrl : profile.Url;
        ApiKeyInput.Clear(); ApiModelsInput.ItemsSource = null; ApiModelsInput.Visibility = Visibility.Collapsed; ApiFeedback.Text = "";
        _loadingProviders = false;
        UpdateProviderFields();
    }
    void UpdateProviderFields()
    {
        if (ApiFields == null) return;
        var id = SelectedTag(ProviderInput); var provider = ApiProviders.Find(id);
        OllamaFields.Visibility = id == "ollama" || provider?.EditableUrl == true ? Visibility.Visible : Visibility.Collapsed;
        ApiFields.Visibility = provider != null ? Visibility.Visible : Visibility.Collapsed;
        AsideFields.Visibility = id == "aside" ? Visibility.Visible : Visibility.Collapsed;
        ModelHelp.Text = provider != null ? "함수 호출을 지원하는 모델을 선택하세요. 화면 이미지 작업에는 이미지 입력도 지원해야 합니다." :
            id == "ollama" ? "함수 호출 지원 모델을 선택하세요. 이미지 작업에는 이미지 입력도 필요합니다. 비우면 첫 설치 모델을 사용합니다." : id == "aside" ? "비우면 Aside의 기본 모델을 사용합니다. 모델을 지정하려면 Aside에 설정된 provider/model 이름을 넣으세요." : "비워두면 이 PC의 로그인 계정에 설정된 기본 모델을 사용합니다.";
        if (provider != null) UpdateKeyStatus();
    }
    void BrainUrlInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ApiFields == null || _loadingProviders || ApiProviders.Find(SelectedTag(ProviderInput)) == null) return;
        _apiRequest?.Cancel(); ApiKeyInput.Clear(); ApiModelsInput.ItemsSource = null;
        ApiModelsInput.Visibility = Visibility.Collapsed; ApiFeedback.Text = "";
        UpdateKeyStatus();
    }
    UiSettings ApiDraft() => new() { BrainProvider = SelectedTag(ProviderInput), BrainModel = ModelInput.Text.Trim(), BrainUrl = BrainUrlInput.Text.Trim() };
    void UpdateKeyStatus()
    {
        var s = ApiDraft(); var provider = ApiProviders.Find(s.BrainProvider);
        if (provider == null) return;
        try
        {
            var destination = ApiProviders.Endpoint(s.BrainProvider, s.BrainUrl);
            ApiDestination.Text = "연결 대상 · " + destination.AbsoluteUri.TrimEnd('/');
            var saved = CredentialStore.Read(ApiProviders.CredentialTarget(s.BrainProvider, s.BrainUrl)) != null;
            ApiKeyStatus.Text = saved ? "이 제공자·주소의 키가 저장되어 있어요. 변경할 때만 입력하세요." :
                provider.RequiresKey ? "저장된 키가 없어요. API 키가 필요합니다." : "저장된 키가 없어요. 서버가 인증을 요구할 때만 입력하세요.";
        }
        catch (Exception ex) { ApiDestination.Text = "연결 주소 확인 필요"; ApiKeyStatus.Text = ex.Message; }
    }
    void SaveApiKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = ApiDraft();
            CredentialStore.Save(ApiProviders.CredentialTarget(s.BrainProvider, s.BrainUrl), ApiKeyInput.Password);
            ApiKeyInput.Clear(); UpdateKeyStatus(); ApiFeedback.Text = "API 키를 저장했어요."; ApiFeedback.Foreground = Brushes.DarkGreen;
        }
        catch (Exception ex) { ApiFeedback.Text = ex.Message; ApiFeedback.Foreground = Brushes.Firebrick; }
    }
    void DeleteApiKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = ApiDraft();
            CredentialStore.Delete(ApiProviders.CredentialTarget(s.BrainProvider, s.BrainUrl));
            ApiKeyInput.Clear(); UpdateKeyStatus(); ApiFeedback.Text = "이 제공자·주소의 키를 삭제했어요."; ApiFeedback.Foreground = Brushes.DarkGreen;
        }
        catch (Exception ex) { ApiFeedback.Text = ex.Message; ApiFeedback.Foreground = Brushes.Firebrick; }
    }
    async Task RunApiSettingRequest(Func<UiSettings, string?, CancellationToken, Task> work)
    {
        if (_apiRequest != null) return;
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        _apiRequest = request;
        ModelsButton.IsEnabled = ApiTestButton.IsEnabled = false; ApiCancelButton.IsEnabled = true;
        ApiFeedback.Text = "연결하는 중…"; ApiFeedback.Foreground = Brushes.DimGray;
        try
        {
            var s = ApiDraft(); var key = ApiKeyInput.Password;
            await work(s, key.Length == 0 ? null : key, request.Token);
        }
        catch (OperationCanceledException) { ApiFeedback.Text = "연결을 취소했거나 대기 시간이 지났어요."; }
        catch (Exception ex) { ApiFeedback.Text = ex.Message; ApiFeedback.Foreground = Brushes.Firebrick; }
        finally { _apiRequest = null; ModelsButton.IsEnabled = ApiTestButton.IsEnabled = true; ApiCancelButton.IsEnabled = false; }
    }
    async void LoadApiModels_Click(object sender, RoutedEventArgs e) => await RunApiSettingRequest(async (s, key, token) =>
    {
        var models = await Brain.ModelsAsync(s, token, key); token.ThrowIfCancellationRequested();
        ApiModelsInput.ItemsSource = models; ApiModelsInput.Visibility = Visibility.Visible;
        ApiFeedback.Text = $"{models.Count}개 모델을 불러왔어요. 대화와 함수 호출을 지원하는 모델을 선택하세요."; ApiFeedback.Foreground = Brushes.DarkGreen;
    });
    async void TestApi_Click(object sender, RoutedEventArgs e) => await RunApiSettingRequest(async (s, key, token) =>
    {
        var plan = await Brain.ApiAsync("Windows 음성 비서의 연결 시험이다. assistant_plan으로 reply는 '연결 성공', actions는 빈 배열로 응답해.", s, token, key);
        token.ThrowIfCancellationRequested();
        if (plan.Actions.Count != 0) throw new InvalidOperationException("서버에 연결됐지만 모델이 연결 시험의 형식을 따르지 않았어요. 다른 대화 모델을 선택하세요.");
        ApiFeedback.Text = Brain.NameFor(s.BrainProvider) + " 연결 성공 · " + plan.Reply; ApiFeedback.Foreground = Brushes.DarkGreen;
    });
    void CancelApi_Click(object sender, RoutedEventArgs e) => _apiRequest?.Cancel();
    void OpenAsideDocs_Click(object sender, RoutedEventArgs e) => WindowsActions.OpenUrl("https://docs.aside.com/help/developers");
    void CancelAside_Click(object sender, RoutedEventArgs e) => _asideRequest?.Cancel();
    async void CheckAside_Click(object sender, RoutedEventArgs e)
    {
        if (_asideRequest != null) return;
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(45)); _asideRequest = request;
        AsideCheckButton.IsEnabled = false; AsideCancelButton.IsEnabled = true;
        AsideFeedback.Text = "Aside CLI와 계정 연결을 확인하고 있어요…"; AsideFeedback.Foreground = Brushes.DimGray;
        try { AsideFeedback.Text = (await new AsideClient().CheckAsync(request.Token)).Summary; AsideFeedback.Foreground = Brushes.DarkGreen; }
        catch (OperationCanceledException) { AsideFeedback.Text = "연결 확인을 취소했거나 대기 시간이 지났어요."; }
        catch (Exception ex) { AsideFeedback.Text = ex.Message; AsideFeedback.Foreground = Brushes.Firebrick; }
        finally { _asideRequest = null; AsideCheckButton.IsEnabled = true; AsideCancelButton.IsEnabled = false; }
    }
    void ApiModelsInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ApiModelsInput.SelectedItem is string model) ModelInput.Text = model;
    }
}
