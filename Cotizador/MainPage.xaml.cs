#pragma warning disable CA1416
#pragma warning disable IL2026
#pragma warning disable CS8622
#pragma warning disable CS8602

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

#if WINDOWS
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
#endif

namespace Cotizador;

public class ItemCotizacion
{
    public int Cantidad { get; set; }
    public string Descripcion { get; set; } = string.Empty;
    public decimal CostoUnitario { get; set; }
    public decimal PrecioUnitario { get; set; }
    public bool EsManoDeObra { get; set; }
    public decimal Total => Cantidad * PrecioUnitario;
    public decimal CostoTotal => Cantidad * CostoUnitario;
    public decimal Ganancia => (PrecioUnitario - CostoUnitario) * Cantidad;
}

public class OpcionMercadoItem
{
    public string Etiqueta { get; set; } = string.Empty;
    public string Titulo { get; set; } = string.Empty;
    public decimal Precio { get; set; }
    public string Tienda { get; set; } = string.Empty;
    public string NombreTienda { get; set; } = string.Empty;
    public string UrlProducto { get; set; } = string.Empty;
    public bool HayResultado { get; set; } = true;
    public string ColorBorde { get; set; } = "#334155";
    public string ColorEtiqueta { get; set; } = "#94A3B8";
    public string ColorTienda { get; set; } = "#22D3EE";

    public string PrecioTexto => HayResultado ? $"${Precio:N0}" : "—";
    public string ColorPrecio => HayResultado ? "#F1F5F9" : "#64748B";
    public string TextoVer => HayResultado ? "🔗 Ver en tienda" : "🔎 Buscar en la tienda";
}

public class CotizacionDto
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Id { get; set; }

    [JsonPropertyName("cliente")]
    public string Cliente { get; set; } = string.Empty;

    [JsonPropertyName("telefono")]
    public string Telefono { get; set; } = string.Empty;

    [JsonPropertyName("equipo")]
    public string Equipo { get; set; } = string.Empty;

    [JsonPropertyName("fecha")]
    public DateTime Fecha { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("total")]
    public decimal Total { get; set; }

    [JsonPropertyName("detalles")]
    public List<ItemCotizacion> Detalles { get; set; } = new();

    [JsonIgnore]
    public string FechaFormateada => Fecha.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

    [JsonIgnore]
    public string TituloResumen => $"{(string.IsNullOrWhiteSpace(Cliente) ? "Cliente General" : Cliente)}  •  {(string.IsNullOrWhiteSpace(Equipo) ? "Equipo" : Equipo)}";

    [JsonIgnore]
    public string SubtituloResumen
    {
        get
        {
            int numConceptos = Detalles?.Sum(x => x.Cantidad) ?? 0;
            decimal gananciaTotal = Detalles?.Sum(x => x.Ganancia) ?? 0m;
            string tel = string.IsNullOrWhiteSpace(Telefono) ? "Sin WhatsApp" : $"Tel: {Telefono}";
            return $"{tel}  |  {numConceptos} concepto(s)  |  Ganancia libre: +${gananciaTotal:N0} MXN";
        }
    }
}

public partial class MainPage : ContentPage
{
    public ObservableCollection<ItemCotizacion> Conceptos { get; set; } = new();
    public ObservableCollection<OpcionMercadoItem> ResultadosMercado { get; set; } = new();
    public ObservableCollection<CotizacionDto> HistorialCotizaciones { get; set; } = new();

    private List<CotizacionDto> _historialCompletoCache = new();

    private static readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    private TaskCompletionSource<bool>? _webViewTcs;

    private bool _modoLaptopActivo = false;

    private string _urlGanadorZegucom = string.Empty;
    private string _urlGanadorDdtech = string.Empty;
    private string _urlGanadorAmazon = string.Empty;
    private string _urlGanadorMercadoLibre = string.Empty;
    private string _urlGanadorCyberpuerta = string.Empty;
    private readonly Dictionary<string, string> _diagTiendas = new();

    // 👇 TUS DATOS DE SUPABASE 👇
    private const string SupabaseUrl = "https://zksrddsciuybspwadsfm.supabase.co/rest/v1/cotizaciones";
    private const string SupabaseApiKey = "sb_publishable_Y3yuPVTszNQDzEz5X6XUSQ_yVv98Ds3";

    public MainPage()
    {
        InitializeComponent();
        listaConceptos.ItemsSource = Conceptos;
        BindableLayout.SetItemsSource(listaResultadosApi, ResultadosMercado);
        listaHistorial.ItemsSource = HistorialCotizaciones;

        pickerManoObra.SelectedIndex = 3;
    }

    private void OnWebExtractorNavigated(object? sender, WebNavigatedEventArgs e)
    {
        _webViewTcs?.TrySetResult(true);
    }

    // =========================================================================================
    // VALIDACIONES EN TIEMPO REAL PARA EVITAR DATOS INCORRECTOS EN CADA CAMPO
    // =========================================================================================

    private void OnSoloLetrasChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not Entry entry || string.IsNullOrEmpty(e.NewTextValue)) return;

        string limpio = new string(e.NewTextValue.Where(c => char.IsLetter(c) || char.IsWhiteSpace(c) || c == '.').ToArray());
        if (limpio != e.NewTextValue)
        {
            entry.Text = limpio;
        }
    }

    private void OnSoloEnterosChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not Entry entry || string.IsNullOrEmpty(e.NewTextValue)) return;

        string limpio = new string(e.NewTextValue.Where(char.IsDigit).ToArray());
        if (limpio != e.NewTextValue)
        {
            entry.Text = limpio;
        }
    }

    private void OnSoloDecimalesPrecioChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not Entry entry || string.IsNullOrEmpty(e.NewTextValue)) return;

        string limpio = LimpiarTextoDecimal(e.NewTextValue);
        if (limpio != e.NewTextValue)
        {
            entry.Text = limpio;
        }
    }

    private void OnCostoInternoChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not Entry entry) return;

        string limpio = LimpiarTextoDecimal(e.NewTextValue ?? "");
        if (limpio != (e.NewTextValue ?? ""))
        {
            entry.Text = limpio;
            return;
        }

        if (decimal.TryParse(limpio, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal costo) && costo > 0)
        {
            decimal sugerido = Math.Ceiling((costo * 1.12m) / 10m) * 10m;
            txtPrecio.Text = sugerido.ToString("0");
        }
    }

    private static string LimpiarTextoDecimal(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        bool yaTienePunto = false;
        var chars = new List<char>(input.Length);

        foreach (char c in input)
        {
            if (char.IsDigit(c))
            {
                chars.Add(c);
            }
            else if ((c == '.' || c == ',') && !yaTienePunto)
            {
                chars.Add('.');
                yaTienePunto = true;
            }
        }

        return new string(chars.ToArray());
    }

    // =========================================================================================
    // MÓDULO DE HISTORIAL DE COTIZACIONES (CONECTADO A SUPABASE)
    // =========================================================================================
    private async void OnToggleHistorialClicked(object? sender, EventArgs e)
    {
        panelHistorial.IsVisible = !panelHistorial.IsVisible;
        if (panelHistorial.IsVisible)
        {
            await CargarHistorialDesdeSupabaseAsync();
        }
    }

    private async void OnRefrescarHistorialClicked(object? sender, EventArgs e)
    {
        await CargarHistorialDesdeSupabaseAsync();
    }

    private void OnCerrarHistorialClicked(object? sender, EventArgs e)
    {
        panelHistorial.IsVisible = false;
    }

    private async Task CargarHistorialDesdeSupabaseAsync()
    {
        try
        {
            lblEstadoHistorial.Text = "⏳ Descargando cotizaciones guardadas desde Supabase...";

            string urlConsulta = $"{SupabaseUrl}?select=*&order=fecha.desc&limit=40";
            using var request = new HttpRequestMessage(HttpMethod.Get, urlConsulta);
            request.Headers.Add("apikey", SupabaseApiKey);
            request.Headers.Add("Authorization", $"Bearer {SupabaseApiKey}");

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                lblEstadoHistorial.Text = $"⚠️ Error al consultar Supabase ({response.StatusCode}). Verifica la política SELECT (RLS).";
                return;
            }

            var listaDescargada = await response.Content.ReadFromJsonAsync<List<CotizacionDto>>();
            _historialCompletoCache = listaDescargada ?? new List<CotizacionDto>();

            AplicarFiltroHistorial(txtBuscarHistorial.Text);

            lblEstadoHistorial.Text = _historialCompletoCache.Count > 0
                ? $"✅ Mostrando {_historialCompletoCache.Count} cotización(es) guardadas en la nube. Toca '📂 Cargar' para editar cualquiera."
                : "📭 Aún no hay cotizaciones guardadas o falta habilitar la política SELECT en Supabase.";
        }
        catch (Exception ex)
        {
            lblEstadoHistorial.Text = $"⚠️ Sin conexión al cargar historial: {ex.Message}";
        }
    }

    private void OnFiltroHistorialChanged(object? sender, TextChangedEventArgs e)
    {
        AplicarFiltroHistorial(e.NewTextValue);
    }

    private void AplicarFiltroHistorial(string? filtro)
    {
        HistorialCotizaciones.Clear();
        string q = filtro?.Trim().ToLowerInvariant() ?? "";

        var filtrados = string.IsNullOrWhiteSpace(q)
            ? _historialCompletoCache
            : _historialCompletoCache.Where(c =>
                (c.Cliente?.ToLowerInvariant().Contains(q) ?? false) ||
                (c.Equipo?.ToLowerInvariant().Contains(q) ?? false) ||
                (c.Telefono?.ToLowerInvariant().Contains(q) ?? false)).ToList();

        foreach (var item in filtrados)
        {
            HistorialCotizaciones.Add(item);
        }
    }

    private void OnCargarCotizacionHistorialClicked(object? sender, EventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is CotizacionDto cot)
        {
            txtCliente.Text = cot.Cliente;
            txtTelefono.Text = cot.Telefono;
            txtEquipo.Text = cot.Equipo;

            Conceptos.Clear();
            if (cot.Detalles != null)
            {
                foreach (var det in cot.Detalles)
                {
                    Conceptos.Add(new ItemCotizacion
                    {
                        Cantidad = det.Cantidad,
                        Descripcion = det.Descripcion,
                        CostoUnitario = det.CostoUnitario,
                        PrecioUnitario = det.PrecioUnitario,
                        EsManoDeObra = det.EsManoDeObra
                    });
                }
            }

            ActualizarTotal();
            panelHistorial.IsVisible = false;
            lblEstadoBd.Text = $"📂 Cotización de '{cot.Cliente}' ({cot.FechaFormateada}) cargada en pantalla.";
        }
    }

    private async void OnEliminarCotizacionHistorialClicked(object? sender, EventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is CotizacionDto cot)
        {
            bool confirmar = await DisplayAlertAsync("Eliminar Cotización",
                $"¿Deseas borrar del historial la cotización de '{cot.Cliente}' por ${cot.Total:N2} MXN?",
                "Sí, borrar", "Cancelar");

            if (!confirmar) return;

            try
            {
                string queryBorrado = cot.Id.HasValue
                    ? $"{SupabaseUrl}?id=eq.{cot.Id.Value}"
                    : $"{SupabaseUrl}?cliente=eq.{Uri.EscapeDataString(cot.Cliente)}&total=eq.{cot.Total}";

                using var request = new HttpRequestMessage(HttpMethod.Delete, queryBorrado);
                request.Headers.Add("apikey", SupabaseApiKey);
                request.Headers.Add("Authorization", $"Bearer {SupabaseApiKey}");

                var res = await _httpClient.SendAsync(request);
                if (res.IsSuccessStatusCode)
                {
                    _historialCompletoCache.Remove(cot);
                    HistorialCotizaciones.Remove(cot);
                    lblEstadoHistorial.Text = $"🗑️ Cotización de '{cot.Cliente}' eliminada de la nube.";
                }
                else
                {
                    await DisplayAlertAsync("Aviso", "No se pudo borrar en Supabase (verifica la política DELETE en RLS).", "OK");
                }
            }
            catch (Exception ex)
            {
                await DisplayAlertAsync("Error", ex.Message, "OK");
            }
        }
    }

    // =========================================================================================
    // INTERRUPTOR MODO PC ESCRITORIO vs MODO LAPTOP (SODIMM)
    // =========================================================================================
    private void OnModoPcClicked(object? sender, EventArgs e)
    {
        _modoLaptopActivo = false;
        panelPiezasLaptop.IsVisible = false;

        btnModoPc.BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#22D3EE");
        btnModoPc.TextColor = Microsoft.Maui.Graphics.Color.FromArgb("#06121A");

        btnModoLaptop.BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#0F172A");
        btnModoLaptop.TextColor = Microsoft.Maui.Graphics.Color.FromArgb("#67E8F9");

        lblEstadoApi.Text = "🖥️ Modo PC Escritorio activo: Buscando componentes estándar de escritorio (RAM DIMM, GPUs, Placas Madre).";
    }

    private void OnModoLaptopClicked(object? sender, EventArgs e)
    {
        _modoLaptopActivo = true;
        panelPiezasLaptop.IsVisible = true;
        panelCompatibilidadAuto.IsVisible = false;

        btnModoLaptop.BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#22D3EE");
        btnModoLaptop.TextColor = Microsoft.Maui.Graphics.Color.FromArgb("#06121A");

        btnModoPc.BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#0F172A");
        btnModoPc.TextColor = Microsoft.Maui.Graphics.Color.FromArgb("#67E8F9");

        lblEstadoApi.Text = "💻 Modo Laptop activo: Toda búsqueda de RAM filtrará exclusivamente módulos cortos SODIMM / SO-DIMM para Laptop.";
    }

    // =========================================================================================
    // 1. AGREGADOR DE SERVICIOS
    // =========================================================================================
    private async void OnAgregarServicioClicked(object? sender, EventArgs e)
    {
        int index = pickerManoObra.SelectedIndex;
        if (index < 0)
        {
            await DisplayAlertAsync("Aviso", "Selecciona un servicio de la lista.", "OK");
            return;
        }

        if (!int.TryParse(txtCantidadServicio.Text?.Trim(), out int cantEquipos) || cantEquipos <= 0 || cantEquipos > 99)
        {
            await DisplayAlertAsync("Cantidad inválida", "Ingresa una cantidad de equipos válida entre 1 y 99.", "OK");
            txtCantidadServicio.Text = "1";
            return;
        }

        ItemCotizacion nuevoServicio = index switch
        {
            0 => new ItemCotizacion
            {
                Cantidad = cantEquipos,
                Descripcion = "Formateo e Instalación de Sistema Operativo",
                CostoUnitario = 0m,
                PrecioUnitario = 400m,
                EsManoDeObra = true
            },
            1 => new ItemCotizacion
            {
                Cantidad = cantEquipos,
                Descripcion = "Formateo y Licenciamiento Oficial (Windows + Office)",
                CostoUnitario = 120m,
                PrecioUnitario = 650m,
                EsManoDeObra = true
            },
            2 => new ItemCotizacion
            {
                Cantidad = cantEquipos,
                Descripcion = "Activación de Licencia Oficial (Windows / Office)",
                CostoUnitario = 100m,
                PrecioUnitario = 300m,
                EsManoDeObra = true
            },
            3 => new ItemCotizacion
            {
                Cantidad = cantEquipos,
                Descripcion = "Mantenimiento Preventivo",
                CostoUnitario = 25m,
                PrecioUnitario = 450m,
                EsManoDeObra = true
            },
            4 => new ItemCotizacion
            {
                Cantidad = cantEquipos,
                Descripcion = "Instalación de Componente (Hardware)",
                CostoUnitario = 15m,
                PrecioUnitario = 350m,
                EsManoDeObra = true
            },
            5 => new ItemCotizacion
            {
                Cantidad = cantEquipos,
                Descripcion = "Actualización de Hardware y Mantenimiento Preventivo",
                CostoUnitario = 25m,
                PrecioUnitario = 550m,
                EsManoDeObra = true
            },
            6 => new ItemCotizacion
            {
                Cantidad = cantEquipos,
                Descripcion = "Actualización de Hardware, Clonación de Sistema y Mantenimiento",
                CostoUnitario = 30m,
                PrecioUnitario = 750m,
                EsManoDeObra = true
            },
            7 => new ItemCotizacion
            {
                Cantidad = cantEquipos,
                Descripcion = "Ensamble Profesional de PC y Configuración BIOS",
                CostoUnitario = 50m,
                PrecioUnitario = 1200m,
                EsManoDeObra = true
            },
            _ => new ItemCotizacion()
        };

        if (index == 0 || index == 1 || index == 2 || index == 6)
            chkSistema.IsChecked = true;
        if (index == 3 || index == 5 || index == 6 || index == 7)
            chkMantenimiento.IsChecked = true;

        Conceptos.Add(nuevoServicio);
        txtCantidadServicio.Text = "1";
        ActualizarTotal();
    }

    private void OnEliminarItemClicked(object? sender, EventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is ItemCotizacion item)
        {
            Conceptos.Remove(item);
            ActualizarTotal();
        }
    }

    // =========================================================================================
    // DETECTOR INTELIGENTE DE PROCESADOR -> ARMA LA GUÍA DE PLACA MADRE, RAM Y GPU
    // =========================================================================================
    private void ActualizarGuiaCompatibilidadSiEsProcesador(string textoBusqueda)
    {
        if (_modoLaptopActivo) return;

        string q = textoBusqueda.ToLowerInvariant();

        bool esRyzenAm5 = Regex.IsMatch(q, @"\b(7500f|7600|7600x|7700|7700x|7800x3d|7900|7900x|7950x|8400f|8500g|8600g|8700g|8700f|9600x|9700x|9800x3d|9900x|9950x)\b") ||
                          (q.Contains("ryzen") && (q.Contains(" 7") || q.Contains(" 8") || q.Contains(" 9")) && Regex.IsMatch(q, @"\b[789]\d{3}"));

        bool esRyzenAm4 = Regex.IsMatch(q, @"\b(3200g|3400g|3600|4500|4600g|5500|5600|5600g|5600x|5600gt|5700|5700g|5700x|5700x3d|5800x|5800x3d|5900x)\b") ||
                          (q.Contains("ryzen") && Regex.IsMatch(q, @"\b[345]\d{3}"));

        bool esIntel1700 = Regex.IsMatch(q, @"\b(12100|12100f|12400|12400f|12600k|12700|13100|13100f|13400|13400f|13600k|13700|14100|14400|14400f|14600k|14700|14900)\b") ||
                           (q.Contains("core i") && Regex.IsMatch(q, @"\b1[234]\d{3}"));

        bool esIntel1851 = q.Contains("core ultra") || Regex.IsMatch(q, @"\b(245k|265k|285k)\b");

        if (esRyzenAm5)
        {
            panelCompatibilidadAuto.IsVisible = true;
            lblTituloCompatibilidad.Text = $"⚡ PIEZAS COMPATIBLES CON TU {textoBusqueda.ToUpper()} (SOCKET AM5):";
            lblResumenCompatibilidad.Text = "✅ Socket: AMD AM5  |  ✅ RAM Obligatoria: DDR5  |  ✅ Puerto GPU: PCIe 4.0 / 5.0 x16 (Compatible con todas las RTX/RX). Fuente sugerida: 650W - 750W.";

            ConfigurarBotonGuia(btnMob1, "Placa Básica: A620", "Tarjeta Madre A620");
            ConfigurarBotonGuia(btnMob2, "Placa Ideal: B650", "Tarjeta Madre B650");
            ConfigurarBotonGuia(btnMob3, "Placa Nueva: B850", "Tarjeta Madre B850");

            ConfigurarBotonGuia(btnGpu1, "GPU Eco: RTX 5050", "RTX 5050");
            ConfigurarBotonGuia(btnGpu2, "GPU Ideal: RTX 5060", "RTX 5060");
            ConfigurarBotonGuia(btnGpu3, "GPU Ultra: RTX 5070", "RTX 5070");
            ConfigurarBotonGuia(btnRamAuto, "RAM: DDR5 16GB", "DDR5 16GB");
        }
        else if (esRyzenAm4)
        {
            panelCompatibilidadAuto.IsVisible = true;
            lblTituloCompatibilidad.Text = $"⚡ PIEZAS COMPATIBLES CON TU {textoBusqueda.ToUpper()} (SOCKET AM4):";
            lblResumenCompatibilidad.Text = "✅ Socket: AMD AM4  |  ✅ RAM Obligatoria: DDR4  |  ✅ Puerto GPU: PCIe x16 (Sin cuello de botella con estas gráficas). Fuente sugerida: 600W - 650W.";

            ConfigurarBotonGuia(btnMob1, "Placa Básica: A520", "Tarjeta Madre A520");
            ConfigurarBotonGuia(btnMob2, "Placa Ideal: B550", "Tarjeta Madre B550");
            ConfigurarBotonGuia(btnMob3, "Placa Eco: B450", "Tarjeta Madre B450");

            ConfigurarBotonGuia(btnGpu1, "GPU Eco: RX 6600", "RX 6600");
            ConfigurarBotonGuia(btnGpu2, "GPU Ideal: RTX 4060", "RTX 4060");
            ConfigurarBotonGuia(btnGpu3, "GPU Nueva: RTX 5060", "RTX 5060");
            ConfigurarBotonGuia(btnRamAuto, "RAM: DDR4 16GB", "DDR4 16GB");
        }
        else if (esIntel1700)
        {
            panelCompatibilidadAuto.IsVisible = true;
            lblTituloCompatibilidad.Text = $"⚡ PIEZAS COMPATIBLES CON TU {textoBusqueda.ToUpper()} (SOCKET LGA1700):";
            lblResumenCompatibilidad.Text = "✅ Socket: Intel LGA1700  |  ✅ RAM: DDR4 o DDR5 (según la placa)  |  ✅ Puerto GPU: PCIe x16. Fuente sugerida: 650W.";

            ConfigurarBotonGuia(btnMob1, "Placa Básica: H610", "Tarjeta Madre H610");
            ConfigurarBotonGuia(btnMob2, "Placa Ideal: B760", "Tarjeta Madre B760");
            ConfigurarBotonGuia(btnMob3, "Placa Alta: Z790", "Tarjeta Madre Z790");

            ConfigurarBotonGuia(btnGpu1, "GPU Eco: RTX 4060", "RTX 4060");
            ConfigurarBotonGuia(btnGpu2, "GPU Ideal: RTX 5060", "RTX 5060");
            ConfigurarBotonGuia(btnGpu3, "GPU AMD: RX 7600", "RX 7600");
            ConfigurarBotonGuia(btnRamAuto, "RAM: DDR5 16GB", "DDR5 16GB");
        }
        else if (esIntel1851)
        {
            panelCompatibilidadAuto.IsVisible = true;
            lblTituloCompatibilidad.Text = $"⚡ PIEZAS COMPATIBLES CON TU {textoBusqueda.ToUpper()} (SOCKET LGA1851):";
            lblResumenCompatibilidad.Text = "✅ Socket: Intel LGA1851  |  ✅ RAM Obligatoria: DDR5  |  ✅ Puerto GPU: PCIe 5.0 x16. Fuente sugerida: 750W.";

            ConfigurarBotonGuia(btnMob1, "Placa Ideal: B860", "Tarjeta Madre B860");
            ConfigurarBotonGuia(btnMob2, "Placa Alta: Z890", "Tarjeta Madre Z890");
            ConfigurarBotonGuia(btnMob3, "SSD: NVMe 1TB", "SSD NVMe 1TB");

            ConfigurarBotonGuia(btnGpu1, "GPU Ideal: RTX 5060", "RTX 5060");
            ConfigurarBotonGuia(btnGpu2, "GPU Alta: RTX 5070", "RTX 5070");
            ConfigurarBotonGuia(btnGpu3, "GPU AMD: RX 7800 XT", "RX 7800 XT");
            ConfigurarBotonGuia(btnRamAuto, "RAM: DDR5 16GB", "DDR5 16GB");
        }
    }

    private void ConfigurarBotonGuia(Button btn, string textoVisible, string busqueda)
    {
        btn.Text = textoVisible;
        btn.CommandParameter = busqueda;
    }

    private void OnBotonSugerenciaClicked(object? sender, EventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is string query)
        {
            txtBuscarApi.Text = query;
            if (query.Contains("DDR", StringComparison.OrdinalIgnoreCase))
                chkRam.IsChecked = true;
            else if (query.Contains("SSD", StringComparison.OrdinalIgnoreCase))
                chkSsd.IsChecked = true;
            else
                chkCpuGpu.IsChecked = true;

            OnBuscarPrecioApiClicked(sender, e);
        }
    }

    private void OnOcultarGuiaClicked(object? sender, EventArgs e)
    {
        panelCompatibilidadAuto.IsVisible = false;
    }

    private async Task<(string Html, string Texto)> ExtraerHtmlYTextoConChromiumAsync(string url, int esperaJsMs = 2400)
    {
        try
        {
            _webViewTcs = new TaskCompletionSource<bool>();
            webExtractor.Source = url;

            await Task.WhenAny(_webViewTcs.Task, Task.Delay(6000));
            await Task.Delay(esperaJsMs);

            string rawHtml = await webExtractor.EvaluateJavaScriptAsync("document.body ? document.body.innerHTML : ''");
            string rawText = await webExtractor.EvaluateJavaScriptAsync("document.body ? document.body.innerText : ''");

            string html = (string.IsNullOrWhiteSpace(rawHtml) || rawHtml == "null") ? string.Empty : Regex.Unescape(rawHtml);
            string texto = (string.IsNullOrWhiteSpace(rawText) || rawText == "null") ? string.Empty : Regex.Unescape(rawText);

            return (html, texto);
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    private async Task AbrirUrlEnNavegadorAsync(string url)
    {
        try
        {
#if WINDOWS
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            await Task.CompletedTask;
#else
            await Launcher.Default.OpenAsync(new Uri(url));
#endif
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Error", ex.Message, "OK");
        }
    }

    private bool EsBusquedaDeTarjetaMadre(string qLower)
    {
        string[] terminosMother =
        {
            "tarjeta madre", "motherboard", "placa madre", "placa base",
            "a520", "b450", "b550", "x570", "a620", "b650", "b850", "x670", "x870",
            "h610", "b660", "b760", "z690", "z790", "b860", "z890", "h510", "b560",
            "am4", "am5", "lga1700", "lga 1700", "lga1851"
        };
        return terminosMother.Any(t => qLower.Contains(t));
    }

    private (string TerminoCorto, string QueryDdtech, string QueryZegucom, string QueryAmazon) PrepararBusquedas(string rawInput)
    {
        string limpio = string.IsNullOrWhiteSpace(rawInput) ? "DDR5" : rawInput.Trim();

        limpio = Regex.Replace(limpio, @"\bddr\s+([345])\b", "DDR$1", RegexOptions.IgnoreCase);
        limpio = Regex.Replace(limpio, @"\bm\s*[\.\-]?\s*2\b", "M.2", RegexOptions.IgnoreCase);
        limpio = Regex.Replace(limpio, @"(\d+)\s+gb\b", "$1GB", RegexOptions.IgnoreCase);
        limpio = Regex.Replace(limpio, @"(\d+)\s+tb\b", "$1TB", RegexOptions.IgnoreCase);
        limpio = Regex.Replace(limpio, @"\bso-?dimm\b", "SODIMM", RegexOptions.IgnoreCase);

        limpio = Regex.Replace(limpio, @"\b(rtx|gtx|rx|ryzen|core)\s*(\d{1,4})", "$1 $2", RegexOptions.IgnoreCase);
        limpio = Regex.Replace(limpio, @"\s+", " ").Trim();

        string q = limpio.ToLowerInvariant();
        string qDdtech = limpio;
        string qZegucom = limpio;
        string qAmazon = limpio;

        bool esRam = (q.Contains("ddr4") || q.Contains("ddr5") || q.Contains("ddr3")) && !EsBusquedaDeTarjetaMadre(q);
        bool pideSodimm = _modoLaptopActivo || q.Contains("laptop") || q.Contains("sodimm");
        bool esSsd = q.Contains("ssd") || q.Contains("nvme") || q.Contains("m.2") || q.Contains("estado solido");

        if (EsBusquedaDeTarjetaMadre(q) && !q.Contains("ryzen") && !q.Contains("core i"))
        {
            string soloChipset = Regex.Replace(limpio, @"\b(tarjeta\s+madre|motherboard|placa\s+madre|placa\s+base)\b", "", RegexOptions.IgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(soloChipset))
                limpio = soloChipset;

            qDdtech = $"Tarjeta Madre {limpio}";
            qZegucom = $"Tarjeta Madre {limpio}";
            qAmazon = $"Tarjeta Madre {limpio}";
        }
        else if (esRam)
        {
            string baseRam = Regex.Replace(limpio, @"\b(laptop|sodimm|so-dimm|memoria|ram)\b", "", RegexOptions.IgnoreCase);
            baseRam = Regex.Replace(baseRam, @"\s+", " ").Trim();

            if (pideSodimm)
            {
                limpio = $"SODIMM {baseRam}";
                qDdtech = $"SO-DIMM {baseRam}";
                qZegucom = $"So-dimm {baseRam}";
                qAmazon = $"Memoria RAM SODIMM {baseRam}";
            }
            else
            {
                limpio = baseRam;
                qDdtech = limpio;
                qZegucom = $"Memoria Ram Dimm {limpio}";
                qAmazon = $"Memoria RAM {limpio}";
            }
        }
        else if (esSsd)
        {
            string capMatch = Regex.Match(limpio, @"\b(\d+(?:GB|TB))\b", RegexOptions.IgnoreCase).Value;
            if (string.IsNullOrEmpty(capMatch)) capMatch = "1TB";

            bool esNvme = q.Contains("nvme") || q.Contains("m.2");
            if (esNvme)
            {
                qDdtech = $"NVMe {capMatch}";
                qZegucom = $"Ssd Nvme M.2 {capMatch}";
                qAmazon = $"SSD NVMe M.2 {capMatch} interno";
            }
            else
            {
                qDdtech = $"Estado Solido {capMatch}";
                qZegucom = $"Ssd {capMatch}";
                qAmazon = $"SSD SATA {capMatch} interno";
            }
        }
        else if ((q.Contains("rtx") || q.Contains("rx ") || q.Contains("geforce") || q.Contains("radeon")) && !q.Contains("laptop"))
        {
            qDdtech = limpio;
            qZegucom = $"Tarjeta de video {limpio}";
            qAmazon = $"GeForce {limpio}";
        }
        else if ((q.Contains("ryzen") || q.Contains("core i") || q.Contains("9600x") || q.Contains("7800x3d") || q.Contains("5600") || q.Contains("5700") || q.Contains("12400") || q.Contains("13400") || q.Contains("14400")) && !q.Contains("laptop"))
        {
            qDdtech = limpio;
            qZegucom = $"Procesador {limpio}";
            qAmazon = $"Procesador {limpio}";
        }

        return (limpio, qDdtech, qZegucom, qAmazon);
    }

    private string ConstruirUrlRealZegucom(string qZegucom)
    {
        string consEncoded = Uri.EscapeDataString(qZegucom);
        string qPlus = Uri.EscapeDataString(qZegucom.ToLowerInvariant()).Replace("%20", "+");
        return $"https://www.zegucom.com.mx/?cons={consEncoded}#271f/embedded/m=and&q={qPlus}";
    }

    private string ConstruirUrlMercadoLibre(string q)
    {
        string slug = Regex.Replace(q.ToLowerInvariant().Trim(), @"[^a-z0-9\.]+", "-").Trim('-');
        return $"https://listado.mercadolibre.com.mx/{slug}_ItemCondition_2230284"; // 2230284 = solo "Nuevo"
    }

    private string ConstruirUrlCyberpuerta(string q)
        => $"https://www.cyberpuerta.mx/index.php?cl=search&searchparam={Uri.EscapeDataString(q).Replace("%20", "+")}";

    private string DetectarEtiquetaSocket(string titulo)
    {
        string t = titulo.ToUpperInvariant();

        if (t.Contains("SODIMM") || t.Contains("SO-DIMM") || t.Contains("LAPTOP") || t.Contains("NOTEBOOK"))
        {
            string tipoRam = t.Contains("DDR5") ? "DDR5" : (t.Contains("DDR4") ? "DDR4" : "");
            return $"  •  💻 LAPTOP SODIMM {tipoRam}".TrimEnd();
        }

        if (t.Contains("NVME") || t.Contains("M.2") || t.Contains("ESTADO SOLIDO") || t.Contains("SSD"))
        {
            return t.Contains("NVME") || t.Contains("PCI-E") || t.Contains("PCIE") ? "  •  ⚡ SSD M.2 NVMe" : "  •  💾 SSD 2.5\" / M.2";
        }

        string socket = "";
        if (t.Contains("AM5") || t.Contains("B650") || t.Contains("B850") || t.Contains("A620") || t.Contains("X670") || t.Contains("X870") || t.Contains("9600X") || t.Contains("7600X") || t.Contains("7800X3D"))
            socket = "AMD AM5";
        else if (t.Contains("AM4") || t.Contains("B550") || t.Contains("A520") || t.Contains("B450") || t.Contains("5600X") || t.Contains("5600G") || t.Contains("5700X"))
            socket = "AMD AM4";
        else if (t.Contains("1851") || t.Contains("B860") || t.Contains("Z890"))
            socket = "INTEL LGA1851";
        else if (t.Contains("1700") || t.Contains("B760") || t.Contains("H610") || t.Contains("Z790") || t.Contains("B660") || t.Contains("12400") || t.Contains("13400") || t.Contains("14400"))
            socket = "INTEL LGA1700";

        string ram = t.Contains("DDR5") ? " • DDR5" : (t.Contains("DDR4") ? " • DDR4" : "");
        return string.IsNullOrEmpty(socket) ? "" : $"  •  🔌 {socket}{ram}";
    }

    // =========================================================================================
    // 3. COMPARADOR DE LAS 5 TIENDAS
    // =========================================================================================
    private async void OnBuscarPrecioApiClicked(object? sender, EventArgs e)
    {
        string busquedaUsuario = txtBuscarApi.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(busquedaUsuario) || busquedaUsuario.Length < 2)
        {
            await DisplayAlertAsync("Aviso", "Escribe al menos 2 caracteres para buscar una pieza (ej. Ryzen 5 9600X, RTX 5060, DDR5 16GB).", "OK");
            return;
        }

        ActualizarGuiaCompatibilidadSiEsProcesador(busquedaUsuario);

        var (terminoCorto, qDdtech, qZegucom, qAmazon) = PrepararBusquedas(busquedaUsuario);

        _urlGanadorDdtech = string.Empty;
        _urlGanadorZegucom = string.Empty;
        _urlGanadorAmazon = string.Empty;
        _urlGanadorMercadoLibre = string.Empty;
        _urlGanadorCyberpuerta = string.Empty;

        _diagTiendas.Clear();
        ResultadosMercado.Clear();
        panelResultadosApi.IsVisible = true;

        var comparativaFinal = new List<(string Titulo, decimal Precio, string Tienda, string UrlDirecta)>();
        var tiendasAgotadas = new List<string>();

        lblTituloResultados.Text = $"⏳ [1/5] Buscando y verificando existencias en DDTech: '{terminoCorto}'...";
        lblEstadoApi.Text = "Saltando accesorios, carcasas y modelos sin stock en DDTech...";

        var mejorDdtech = await ObtenerCampeonDdtechVerificadoAsync(terminoCorto, qDdtech);
        if (mejorDdtech.HasValue && mejorDdtech.Value.Precio > 0 && !string.IsNullOrWhiteSpace(mejorDdtech.Value.Titulo))
        {
            comparativaFinal.Add(mejorDdtech.Value);
            _urlGanadorDdtech = mejorDdtech.Value.UrlDirecta;
        }
        else
        {
            tiendasAgotadas.Add("DDTech");
        }

        lblTituloResultados.Text = $"⏳ [2/5] Leyendo 'Precio Web' en Zegucom: '{qZegucom}'...";
        lblEstadoApi.Text = "Extrayendo Precio Web oficial y link del producto en Zegucom...";

        var mejorZegucom = await ObtenerCampeonZegucomPrecioWebConLinkAsync(terminoCorto, qZegucom);
        if (mejorZegucom.HasValue && mejorZegucom.Value.Precio > 0 && !string.IsNullOrWhiteSpace(mejorZegucom.Value.Titulo))
        {
            comparativaFinal.Add(mejorZegucom.Value);
            _urlGanadorZegucom = mejorZegucom.Value.UrlDirecta;
        }
        else
        {
            tiendasAgotadas.Add("Zegucom");
        }

        lblTituloResultados.Text = $"⏳ [3/5] Buscando en Amazon MX: '{qAmazon}'...";
        lblEstadoApi.Text = "Extrayendo precio y link directo en Amazon México...";

        var mejorAmazon = await ObtenerCampeonAmazonConLinkAsync(terminoCorto, qAmazon);
        if (mejorAmazon.HasValue && mejorAmazon.Value.Precio > 0 && !string.IsNullOrWhiteSpace(mejorAmazon.Value.Titulo))
        {
            comparativaFinal.Add(mejorAmazon.Value);
            _urlGanadorAmazon = mejorAmazon.Value.UrlDirecta;
        }
        else
        {
            tiendasAgotadas.Add("Amazon MX");
        }

        lblTituloResultados.Text = $"⏳ [4/5] Buscando en Mercado Libre: '{qAmazon}'...";
        lblEstadoApi.Text = "Filtrando usados y publicaciones dudosas en Mercado Libre...";

        var mejorMl = await ObtenerCampeonMercadoLibreAsync(terminoCorto, qAmazon);
        if (mejorMl.HasValue && mejorMl.Value.Precio > 0 && !string.IsNullOrWhiteSpace(mejorMl.Value.Titulo))
        {
            comparativaFinal.Add(mejorMl.Value);
            _urlGanadorMercadoLibre = mejorMl.Value.UrlDirecta;
        }
        else
        {
            tiendasAgotadas.Add("Mercado Libre");
        }

        lblTituloResultados.Text = $"⏳ [5/5] Buscando en Cyberpuerta: '{qZegucom}'...";
        lblEstadoApi.Text = "Extrayendo precio y link directo en Cyberpuerta...";

        var mejorCp = await ObtenerCampeonCyberpuertaAsync(terminoCorto, qZegucom);
        if (mejorCp.HasValue && mejorCp.Value.Precio > 0 && !string.IsNullOrWhiteSpace(mejorCp.Value.Titulo))
        {
            comparativaFinal.Add(mejorCp.Value);
            _urlGanadorCyberpuerta = mejorCp.Value.UrlDirecta;
        }
        else
        {
            tiendasAgotadas.Add("Cyberpuerta");
        }

        comparativaFinal = comparativaFinal
            .Where(x => x.Precio > 0 && !string.IsNullOrWhiteSpace(x.Titulo))
            .OrderBy(x => x.Precio)
            .ToList();

        string[] todasLasTiendas = { "Zegucom", "DDTech", "Amazon MX", "Mercado Libre", "Cyberpuerta" };

        if (comparativaFinal.Count > 0)
        {
            decimal precioMinimoGlobal = comparativaFinal.First().Precio;

            for (int i = 0; i < comparativaFinal.Count; i++)
            {
                var item = comparativaFinal[i];
                string etiqueta;
                string colorBorde;
                string colorEtiqueta;

                if (item.Precio == precioMinimoGlobal)
                {
                    etiqueta = i == 0 ? "🏆 1° MÁS BARATO" : "🏆 1° EMPATE";
                    colorBorde = "#34D399";
                    colorEtiqueta = "#34D399";
                }
                else if (i > 0 && i < comparativaFinal.Count - 1)
                {
                    etiqueta = $"{i + 1}° PRECIO MEDIO";
                    colorBorde = "#475569";
                    colorEtiqueta = "#94A3B8";
                }
                else
                {
                    etiqueta = $"{i + 1}° MÁS CARO";
                    colorBorde = "#BE123C";
                    colorEtiqueta = "#FB7185";
                }

                string infoSocket = DetectarEtiquetaSocket(item.Titulo);
                string estadoStock = item.Tienda == "Mercado Libre" ? "🛒 Publicación activa" : "✅ Stock verificado";

                ResultadosMercado.Add(new OpcionMercadoItem
                {
                    Etiqueta = etiqueta,
                    Titulo = item.Titulo,
                    Precio = item.Precio,
                    NombreTienda = item.Tienda,
                    Tienda = $"🆕 Nuevo  •  {estadoStock}{infoSocket}",
                    UrlProducto = item.UrlDirecta,
                    HayResultado = true,
                    ColorBorde = colorBorde,
                    ColorEtiqueta = colorEtiqueta,
                    ColorTienda = ColorDeTienda(item.Tienda)
                });
            }
        }

        // Las tiendas sin resultado también aparecen, con un botón para buscar a mano
        foreach (var tienda in todasLasTiendas)
        {
            if (comparativaFinal.Any(x => x.Tienda == tienda)) continue;

            string motivo = _diagTiendas.TryGetValue(tienda, out var d) ? d : "sin stock o sin coincidencia exacta";

            ResultadosMercado.Add(new OpcionMercadoItem
            {
                Etiqueta = "SIN RESULTADOS NUEVOS",
                Titulo = $"No se encontró una pieza nueva con stock para '{terminoCorto}'",
                Precio = 0,
                NombreTienda = tienda,
                Tienda = motivo,
                UrlProducto = string.Empty,
                HayResultado = false,
                ColorBorde = "#334155",
                ColorEtiqueta = "#94A3B8",
                ColorTienda = ColorDeTienda(tienda)
            });
        }

        if (comparativaFinal.Count == 0)
        {
            lblTituloResultados.Text = $"⚠️ Sin piezas nuevas con stock para '{terminoCorto}' en las 5 tiendas.";
            lblEstadoApi.Text = "Usa el botón de cada tarjeta para buscar manualmente en la tienda.";
            txtDescripcion.Text = qZegucom;
            return;
        }

        var ganador = comparativaFinal.First();
        string tiendasEncontradas = string.Join(" vs ", comparativaFinal.Select(x => x.Tienda));
        string notaSinStock = tiendasAgotadas.Count > 0 ? $" (Sin resultados en: {string.Join(", ", tiendasAgotadas)})" : "";

        lblTituloResultados.Text = $"Comparativa de 5 tiendas para '{terminoCorto}' (solo nuevo):";
        lblEstadoApi.Text = $"Más barato hoy: {ganador.Tienda} (${ganador.Precio:N2} MXN).{notaSinStock}";
    }

    private bool BloqueIndicaAgotado(string fragmentoCorto)
    {
        if (string.IsNullOrWhiteSpace(fragmentoCorto)) return false;
        string lower = Regex.Replace(fragmentoCorto.ToLowerInvariant(), @"\s+", " ");

        string[] indicadoresSinStock =
        {
            "sin existencia", "sin stock", "sinstock", "agotado", "out of stock",
            "no disponible", "temporalmente sin", "0 disponibles", "disponibles: 0",
            "disponibles:0", "disponibles 0", "cantidad: sin stock", "sobre pedido", "producto agotado"
        };

        return indicadoresSinStock.Any(ind => lower.Contains(ind));
    }

    private List<(string Titulo, decimal Precio, string Tienda, string UrlDirecta)> OrdenarCandidatosPreferidos(
        List<(string Titulo, decimal Precio, string Tienda, string UrlDirecta)> candidatos, string terminoCorto)
    {
        var validos = candidatos.Where(x => x.Precio > 0 && !string.IsNullOrWhiteSpace(x.Titulo)).ToList();
        if (validos.Count == 0) return validos;

        string qLower = terminoCorto.ToLowerInvariant();
        bool usuarioPidioTi = Regex.IsMatch(qLower, @"\bti\b");
        bool usuarioPidioSuper = qLower.Contains("super");

        if (!usuarioPidioTi && !usuarioPidioSuper)
        {
            var exactosSinTi = validos
                .Where(x => !Regex.IsMatch(x.Titulo.ToLowerInvariant(), @"\b(ti|super)\b") &&
                            !x.Titulo.ToLowerInvariant().Contains("5060ti") &&
                            !x.Titulo.ToLowerInvariant().Contains("4060ti"))
                .OrderBy(x => x.Precio)
                .ToList();

            var conTi = validos
                .Except(exactosSinTi)
                .OrderBy(x => x.Precio)
                .ToList();

            return exactosSinTi.Concat(conTi).ToList();
        }

        return validos.OrderBy(x => x.Precio).ToList();
    }

    private (string Titulo, decimal Precio, string Tienda, string UrlDirecta)? ElegirMejorCoincidenciaConStock(
        List<(string Titulo, decimal Precio, string Tienda, string UrlDirecta)> candidatos, string terminoCorto)
    {
        var ordenados = OrdenarCandidatosPreferidos(candidatos, terminoCorto);
        if (ordenados.Count == 0) return null;
        return ordenados[0];
    }

    private async Task<(string Titulo, decimal Precio, string Tienda, string UrlDirecta)?> ObtenerCampeonDdtechVerificadoAsync(string terminoCorto, string qDdtech)
    {
        var candidatosIniciales = new List<(string Titulo, decimal Precio, string Tienda, string UrlDirecta)>();
        var (minPrecio, maxPrecio) = ObtenerRangoPreciosCoherente(terminoCorto);

        var palabras = terminoCorto.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string palabraModelo = palabras.LastOrDefault(p => p.Any(char.IsDigit)) ?? terminoCorto;

        var urlsIntento = new List<string>
        {
            $"https://ddtech.mx/buscar/{Uri.EscapeDataString(terminoCorto).Replace("%20", "+")}",
            $"https://ddtech.mx/buscar/{Uri.EscapeDataString(qDdtech).Replace("%20", "+")}"
        };

        if (palabraModelo != terminoCorto && palabraModelo.Length >= 4)
        {
            urlsIntento.Add($"https://ddtech.mx/buscar/{Uri.EscapeDataString(palabraModelo)}");
        }

        if (terminoCorto.Contains("SODIMM", StringComparison.OrdinalIgnoreCase))
        {
            string sinSodimm = Regex.Replace(terminoCorto, @"\bsodimm\b", "", RegexOptions.IgnoreCase).Trim();
            urlsIntento.Insert(0, $"https://ddtech.mx/buscar/SO-DIMM+{Uri.EscapeDataString(sinSodimm).Replace("%20", "+")}");
            urlsIntento.Add($"https://ddtech.mx/buscar/Memoria+RAM+{Uri.EscapeDataString(sinSodimm).Replace("%20", "+")}");
        }
        else if (qDdtech.StartsWith("Tarjeta Madre", StringComparison.OrdinalIgnoreCase))
        {
            urlsIntento.Insert(0, $"https://ddtech.mx/buscar/Tarjeta+Madre+{Uri.EscapeDataString(terminoCorto).Replace("%20", "+")}");
        }

        try
        {
            foreach (var urlBusquedaDdtech in urlsIntento.Distinct())
            {
                if (candidatosIniciales.Count >= 2) break;

                var (html, texto) = await ExtraerHtmlYTextoConChromiumAsync(urlBusquedaDdtech, 2300);

                if (!string.IsNullOrWhiteSpace(html))
                {
                    var matches = Regex.Matches(html, @"title=""([^""]{12,240})""([\s\S]{1,1400}?)\$\s*([0-9]{1,3}(?:,[0-9]{3})+|[0-9]{3,5})(?:\.([0-9]{2}))?", RegexOptions.IgnoreCase);
                    foreach (Match m in matches)
                    {
                        string nombre = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
                        string antesDelPrecio = m.Groups[2].Value;
                        string numEntero = m.Groups[3].Value.Replace(",", "");
                        string centavos = m.Groups[4].Success ? "." + m.Groups[4].Value : "";

                        int finMatch = m.Index + m.Length;
                        string despues = html.Substring(finMatch, Math.Min(350, html.Length - finMatch));
                        int idxSig = despues.IndexOf("title=\"", StringComparison.OrdinalIgnoreCase);
                        if (idxSig > 0) despues = despues.Substring(0, idxSig);

                        if (BloqueIndicaAgotado(antesDelPrecio + " " + despues))
                            continue;

                        string ventanaLink = html.Substring(Math.Max(0, m.Index - 500), Math.Min(500, m.Index)) + antesDelPrecio + despues;
                        var matchHref = Regex.Match(ventanaLink, @"href=""(https?://(?:www\.)?ddtech\.mx/producto/[^""]+)""", RegexOptions.IgnoreCase);
                        string linkProducto = matchHref.Success ? matchHref.Groups[1].Value : urlBusquedaDdtech;

                        if (decimal.TryParse(numEntero + centavos, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal p) && p >= minPrecio && p <= maxPrecio)
                        {
                            if (CumpleFiltroComponente(nombre, terminoCorto))
                            {
                                if (!candidatosIniciales.Any(x => x.Titulo.Equals(nombre, StringComparison.OrdinalIgnoreCase)))
                                    candidatosIniciales.Add((nombre, p, "DDTech", linkProducto));
                            }
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(texto))
                {
                    var desdeTexto = ExtraerDesdeTextoPlanoConLink(texto, html, terminoCorto, "DDTech", urlBusquedaDdtech);
                    foreach (var item in desdeTexto)
                    {
                        if (!candidatosIniciales.Any(x => x.Titulo.Equals(item.Titulo, StringComparison.OrdinalIgnoreCase)))
                            candidatosIniciales.Add(item);
                    }
                }
            }

            var candidatosOrdenados = OrdenarCandidatosPreferidos(candidatosIniciales, terminoCorto);

            foreach (var cand in candidatosOrdenados.Take(4))
            {
                if (!cand.UrlDirecta.Contains("/producto/", StringComparison.OrdinalIgnoreCase))
                    return cand;

                lblEstadoApi.Text = $"Verificando stock en DDTech de: {cand.Titulo.Substring(0, Math.Min(35, cand.Titulo.Length))}...";
                var (htmlFicha, textoFicha) = await ExtraerHtmlYTextoConChromiumAsync(cand.UrlDirecta, 1600);

                string combinado = (textoFicha + "\n" + htmlFicha);
                if (!string.IsNullOrWhiteSpace(combinado))
                {
                    int idxRel = combinado.IndexOf("PRODUCTOS RELACIONADOS", StringComparison.OrdinalIgnoreCase);
                    if (idxRel > 0) combinado = combinado.Substring(0, idxRel);

                    string planoFicha = Regex.Replace(combinado, @"<[^>]+>", " ");
                    planoFicha = Regex.Replace(planoFicha.ToLowerInvariant(), @"\s+", " ");

                    if (planoFicha.Contains("sin stock") ||
                        Regex.IsMatch(planoFicha, @"disponibles:\s*0\b"))
                    {
                        continue;
                    }
                }

                return cand;
            }
        }
        catch { }

        return null;
    }

    private async Task<(string Titulo, decimal Precio, string Tienda, string UrlDirecta)?> ObtenerCampeonZegucomPrecioWebConLinkAsync(string terminoCorto, string qZegucom)
    {
        var encontrados = new List<(string Titulo, decimal Precio, string Tienda, string UrlDirecta)>();
        var (minPrecio, maxPrecio) = ObtenerRangoPreciosCoherente(terminoCorto);

        try
        {
            webExtractor.Source = "about:blank";
            await Task.Delay(150);

            string urlEmbedded = ConstruirUrlRealZegucom(qZegucom);
            _webViewTcs = new TaskCompletionSource<bool>();
            webExtractor.Source = urlEmbedded;

            await Task.WhenAny(_webViewTcs.Task, Task.Delay(6000));

            string textoZegu = string.Empty;
            string htmlZegu = string.Empty;

            for (int i = 0; i < 8; i++)
            {
                await Task.Delay(500);
                string raw = await webExtractor.EvaluateJavaScriptAsync("document.body ? document.body.innerText : ''");
                if (!string.IsNullOrWhiteSpace(raw) && raw != "null")
                {
                    textoZegu = Regex.Unescape(raw);
                    if (textoZegu.Contains("Precio Web", StringComparison.OrdinalIgnoreCase))
                    {
                        string rawH = await webExtractor.EvaluateJavaScriptAsync("document.body ? document.body.innerHTML : ''");
                        if (!string.IsNullOrWhiteSpace(rawH) && rawH != "null")
                            htmlZegu = Regex.Unescape(rawH);
                        break;
                    }
                }
            }

            var linksZegucom = new List<(string Contexto, string Url)>();
            if (!string.IsNullOrWhiteSpace(htmlZegu))
            {
                var matchesLinks = Regex.Matches(htmlZegu, @"href=""((?:https?://(?:www\.)?zegucom\.com\.mx)?/producto/[^""]+)""[^>]*>([\s\S]{1,400}?)</a>", RegexOptions.IgnoreCase);
                foreach (Match ml in matchesLinks)
                {
                    string href = ml.Groups[1].Value;
                    if (href.StartsWith("/")) href = "https://www.zegucom.com.mx" + href;
                    string ctx = WebUtility.HtmlDecode(Regex.Replace(ml.Groups[2].Value, @"<[^>]+>", " ")).Trim();
                    linksZegucom.Add((ctx, href));
                }
            }

            if (!string.IsNullOrWhiteSpace(textoZegu))
            {
                var lineas = textoZegu.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                      .Select(l => l.Trim())
                                      .Where(l => l.Length > 1)
                                      .ToList();

                for (int i = 0; i < lineas.Count; i++)
                {
                    if (!lineas[i].Contains("Precio Web", StringComparison.OrdinalIgnoreCase))
                        continue;

                    decimal precioWebReal = 0;
                    for (int k = i; k <= Math.Min(lineas.Count - 1, i + 2); k++)
                    {
                        if (lineas[k].Contains("CLUB", StringComparison.OrdinalIgnoreCase))
                            break;

                        var mPrecio = Regex.Match(lineas[k], @"\$\s*([0-9]{1,3}(?:,[0-9]{3})+|[0-9]{3,5})(?:\.([0-9]{2}))?");
                        if (mPrecio.Success)
                        {
                            string ent = mPrecio.Groups[1].Value.Replace(",", "");
                            string dec = mPrecio.Groups[2].Success ? "." + mPrecio.Groups[2].Value : "";
                            if (decimal.TryParse(ent + dec, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal pTemp))
                            {
                                precioWebReal = pTemp;
                                break;
                            }
                        }
                    }

                    if (precioWebReal < minPrecio || precioWebReal > maxPrecio)
                        continue;

                    for (int j = i - 1; j >= Math.Max(0, i - 5); j--)
                    {
                        string linTitulo = lineas[j];
                        if (linTitulo.Contains("Envío gratis", StringComparison.OrdinalIgnoreCase) ||
                            linTitulo.Contains("Precio", StringComparison.OrdinalIgnoreCase) ||
                            linTitulo.Contains("Remate", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (CumpleFiltroComponente(linTitulo, terminoCorto))
                        {
                            string modeloArriba = j - 1 >= 0 && lineas[j - 1].Length >= 5 && lineas[j - 1].Length <= 30 ? lineas[j - 1] : linTitulo;
                            string urlPiezaZegu = ConstruirUrlRealZegucom(modeloArriba);

                            var matchLink = linksZegucom.FirstOrDefault(x =>
                                x.Contexto.Contains(modeloArriba, StringComparison.OrdinalIgnoreCase) ||
                                linTitulo.Split(' ').Take(4).All(pal => x.Contexto.Contains(pal, StringComparison.OrdinalIgnoreCase)));

                            if (!string.IsNullOrEmpty(matchLink.Url))
                                urlPiezaZegu = matchLink.Url;

                            if (!encontrados.Any(x => x.Titulo.Equals(linTitulo, StringComparison.OrdinalIgnoreCase)))
                            {
                                encontrados.Add((linTitulo, precioWebReal, "Zegucom", urlPiezaZegu));
                            }
                            break;
                        }
                    }
                }

                if (encontrados.Count == 0)
                {
                    encontrados.AddRange(ExtraerDesdeTextoPlanoConLink(textoZegu, htmlZegu, terminoCorto, "Zegucom", urlEmbedded));
                }
            }
        }
        catch { }

        return ElegirMejorCoincidenciaConStock(encontrados, terminoCorto);
    }

    private async Task<(string Titulo, decimal Precio, string Tienda, string UrlDirecta)?> ObtenerCampeonAmazonConLinkAsync(string terminoCorto, string qAmazon)
    {
        var encontrados = new List<(string Titulo, decimal Precio, string Tienda, string UrlDirecta)>();
        var (minPrecio, maxPrecio) = ObtenerRangoPreciosCoherente(terminoCorto);

        try
        {
            string urlAmazon = $"https://www.amazon.com.mx/s?k={Uri.EscapeDataString(qAmazon)}";
            var (html, texto) = await ExtraerHtmlYTextoConChromiumAsync(urlAmazon, 2400);

            if (!string.IsNullOrWhiteSpace(html))
            {
                var bloquesH2 = Regex.Matches(html, @"<h2[^>]*>([\s\S]{10,600}?)</h2>([\s\S]{1,1800}?)\$\s*([0-9]{1,3}(?:,[0-9]{3})+|[0-9]{3,5})(?:\.([0-9]{2}))?");
                foreach (Match m in bloquesH2)
                {
                    int finMatch = m.Index + m.Length;
                    string despues = html.Substring(finMatch, Math.Min(350, html.Length - finMatch));
                    string interiorH2 = m.Groups[1].Value;
                    string contextoTotal = m.Groups[2].Value + " " + despues;

                    if (BloqueIndicaAgotado(contextoTotal)) continue;

                    string linkProductoAmazon = urlAmazon;
                    var matchHref = Regex.Match(interiorH2 + " " + m.Groups[2].Value, @"href=""(/[^""]*?/dp/[A-Z0-9]{10}[^""]*)""", RegexOptions.IgnoreCase);
                    if (matchHref.Success)
                    {
                        linkProductoAmazon = "https://www.amazon.com.mx" + WebUtility.HtmlDecode(matchHref.Groups[1].Value);
                    }

                    string tituloCrudo = Regex.Replace(interiorH2, @"<[^>]+>", " ");
                    string nombre = WebUtility.HtmlDecode(tituloCrudo).Replace("\r", " ").Replace("\n", " ").Trim();
                    nombre = Regex.Replace(nombre, @"\s+", " ");

                    string numEntero = m.Groups[3].Value.Replace(",", "");
                    string centavos = m.Groups[4].Success ? "." + m.Groups[4].Value : "";

                    if (decimal.TryParse(numEntero + centavos, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal p) && p >= minPrecio && p <= maxPrecio)
                    {
                        if (CumpleFiltroComponente(nombre, terminoCorto))
                            encontrados.Add((nombre, p, "Amazon MX", linkProductoAmazon));
                    }
                }
            }

            if (encontrados.Count == 0 && !string.IsNullOrWhiteSpace(texto))
            {
                encontrados.AddRange(ExtraerDesdeTextoPlanoConLink(texto, html, terminoCorto, "Amazon MX", urlAmazon));
            }
        }
        catch { }

        return ElegirMejorCoincidenciaConStock(encontrados, terminoCorto);
    }

    // =========================================================================================
    // NUEVAS TIENDAS: MERCADO LIBRE Y CYBERPUERTA
    // =========================================================================================
    private static bool TryParsePrecioMx(string ent, string? dec, out decimal precio)
    {
        string limpio = ent.Replace(",", "") + (string.IsNullOrEmpty(dec) ? "" : "." + dec);
        return decimal.TryParse(limpio, NumberStyles.Any, CultureInfo.InvariantCulture, out precio);
    }

    // Edita esta lista si quieres excluir más cosas (ej. agrega "sin caja" para descartar procesadores tipo tray)
    private static readonly string[] TerminosNoNuevo =
    {
        "usado", "usada", "reacondicionado", "reacondicionada", "renovado", "renovada", "refurbished",
        "renewed", "open box", "openbox", "seminuevo", "segunda mano", "pre-owned", "preowned",
        "como nuevo", "restaurado", "para reparar", "para refacciones", "para piezas"
    };

    private static bool EsPublicacionDeSegundaMano(string titulo)
    {
        string t = titulo.ToLowerInvariant();
        return TerminosNoNuevo.Any(n => t.Contains(n));
    }

    private static string ColorDeTienda(string tienda) => tienda switch
    {
        "Zegucom" => "#A78BFA",
        "DDTech" => "#38BDF8",
        "Amazon MX" => "#FB923C",
        "Mercado Libre" => "#FACC15",
        "Cyberpuerta" => "#34D399",
        _ => "#22D3EE"
    };

    private async Task<(string Titulo, decimal Precio, string Tienda, string UrlDirecta)?> ObtenerCampeonMercadoLibreAsync(string terminoCorto, string qMl)
    {
        var encontrados = new List<(string Titulo, decimal Precio, string Tienda, string UrlDirecta)>();
        var (minPrecio, maxPrecio) = ObtenerRangoPreciosCoherente(terminoCorto);

        try
        {
            string urlMl = ConstruirUrlMercadoLibre(qMl);
            var (html, texto) = await ExtraerHtmlYTextoConChromiumAsync(urlMl, 3200);
            _diagTiendas["Mercado Libre"] = "la página no cargó (bloqueo o tiempo agotado)";

            if (!string.IsNullOrWhiteSpace(html))
            {
                var titulos = Regex.Matches(html, @"<a\b[^>]*poly-component__title[^>]*>([\s\S]*?)</a>", RegexOptions.IgnoreCase);
                _diagTiendas["Mercado Libre"] = $"{titulos.Count} publicaciones leídas, ninguna nueva con ese modelo y rango de precio";

                for (int idx = 0; idx < titulos.Count; idx++)
                {
                    Match mt = titulos[idx];
                    int ini = mt.Index + mt.Length;
                    int fin = idx + 1 < titulos.Count ? titulos[idx + 1].Index : Math.Min(html.Length, ini + 4000);
                    string ventana = html.Substring(ini, Math.Max(0, Math.Min(fin - ini, 4000)));

                    string nombre = WebUtility.HtmlDecode(Regex.Replace(mt.Groups[1].Value, @"<[^>]+>", " "));
                    nombre = Regex.Replace(nombre, @"\s+", " ").Trim();

                    if (EsPublicacionDeSegundaMano(nombre) || BloqueIndicaAgotado(ventana)) continue;

                    // Toma el primer precio que NO sea el tachado ("antes")
                    decimal precio = 0;
                    var montos = Regex.Matches(ventana,
                        @"<span[^>]*class=""andes-money-amount([^""]*)""[\s\S]*?andes-money-amount__fraction[^>]*>\s*([0-9][0-9,\.]*)\s*</span>");
                    foreach (Match mm in montos)
                    {
                        if (mm.Groups[1].Value.Contains("previous", StringComparison.OrdinalIgnoreCase)) continue;
                        string ent = mm.Groups[2].Value.Replace(",", "").Replace(".", "");
                        if (TryParsePrecioMx(ent, null, out precio)) break;
                    }

                    if (precio < minPrecio || precio > maxPrecio) continue;
                    if (!CumpleFiltroComponente(nombre, terminoCorto)) continue;

                    var mh = Regex.Match(mt.Value, @"href=""([^""]+)""", RegexOptions.IgnoreCase);
                    string link = mh.Success ? WebUtility.HtmlDecode(mh.Groups[1].Value) : urlMl;

                    if (!encontrados.Any(x => x.Titulo.Equals(nombre, StringComparison.OrdinalIgnoreCase)))
                        encontrados.Add((nombre, precio, "Mercado Libre", link));
                }
            }

            if (encontrados.Count == 0 && !string.IsNullOrWhiteSpace(texto))
            {
                encontrados.AddRange(ExtraerDesdeTextoPlanoConLink(texto, html, terminoCorto, "Mercado Libre", urlMl)
                    .Where(x => !EsPublicacionDeSegundaMano(x.Titulo)));
            }
        }
        catch { }

        return ElegirMejorCoincidenciaConStock(encontrados, terminoCorto);
    }

    private async Task<(string Titulo, decimal Precio, string Tienda, string UrlDirecta)?> ObtenerCampeonCyberpuertaAsync(string terminoCorto, string qCp)
    {
        var encontrados = new List<(string Titulo, decimal Precio, string Tienda, string UrlDirecta)>();
        var (minPrecio, maxPrecio) = ObtenerRangoPreciosCoherente(terminoCorto);

        try
        {
            string urlCp = ConstruirUrlCyberpuerta(qCp);
            var (html, texto) = await ExtraerHtmlYTextoConChromiumAsync(urlCp, 3800);
            _diagTiendas["Cyberpuerta"] = "la página no cargó (bloqueo o tiempo agotado)";

            if (!string.IsNullOrWhiteSpace(html))
            {
                var titulos = Regex.Matches(html, @"<a\b[^>]*emproduct_right_title[^>]*>([\s\S]*?)</a>", RegexOptions.IgnoreCase);
                _diagTiendas["Cyberpuerta"] = $"{titulos.Count} productos leídos, ninguno nuevo con ese modelo y rango de precio";

                for (int idx = 0; idx < titulos.Count; idx++)
                {
                    Match mt = titulos[idx];
                    int ini = mt.Index + mt.Length;
                    int fin = idx + 1 < titulos.Count ? titulos[idx + 1].Index : Math.Min(html.Length, ini + 3000);
                    string ventana = html.Substring(ini, Math.Max(0, Math.Min(fin - ini, 3000)));

                    string nombre = WebUtility.HtmlDecode(Regex.Replace(mt.Groups[1].Value, @"<[^>]+>", " "));
                    nombre = Regex.Replace(nombre, @"\s+", " ").Trim();

                    if (BloqueIndicaAgotado(ventana)) continue;

                    var mp = Regex.Match(ventana, @"\$\s*([0-9]{1,3}(?:,[0-9]{3})+|[0-9]{3,5})(?:\.([0-9]{2}))?");
                    if (!mp.Success) continue;
                    if (!TryParsePrecioMx(mp.Groups[1].Value, mp.Groups[2].Success ? mp.Groups[2].Value : null, out decimal precio)) continue;
                    if (precio < minPrecio || precio > maxPrecio) continue;
                    if (!CumpleFiltroComponente(nombre, terminoCorto)) continue;

                    var mh = Regex.Match(mt.Value, @"href=""([^""]+)""", RegexOptions.IgnoreCase);
                    string link = urlCp;
                    if (mh.Success)
                    {
                        link = WebUtility.HtmlDecode(mh.Groups[1].Value);
                        if (link.StartsWith("/")) link = "https://www.cyberpuerta.mx" + link;
                    }

                    if (!encontrados.Any(x => x.Titulo.Equals(nombre, StringComparison.OrdinalIgnoreCase)))
                        encontrados.Add((nombre, precio, "Cyberpuerta", link));
                }
            }

            if (encontrados.Count == 0 && !string.IsNullOrWhiteSpace(texto))
            {
                encontrados.AddRange(ExtraerDesdeTextoPlanoConLink(texto, html, terminoCorto, "Cyberpuerta", urlCp));
            }
        }
        catch { }

        return ElegirMejorCoincidenciaConStock(encontrados, terminoCorto);
    }

    private List<(string Titulo, decimal Precio, string Tienda, string UrlDirecta)> ExtraerDesdeTextoPlanoConLink(
        string texto, string html, string terminoCorto, string nombreTienda, string urlFallback)
    {
        var lista = new List<(string Titulo, decimal Precio, string Tienda, string UrlDirecta)>();
        var (minPrecio, maxPrecio) = ObtenerRangoPreciosCoherente(terminoCorto);

        var lineas = texto.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                          .Select(l => l.Trim())
                          .Where(l => l.Length > 1)
                          .ToList();

        for (int i = 0; i < lineas.Count; i++)
        {
            string linLower = lineas[i].ToLowerInvariant();
            if (linLower.Contains("meses") || linLower.Contains("msi") || linLower.Contains("envío") || linLower.Contains("club"))
                continue;

            if (BloqueIndicaAgotado(lineas[i])) continue;

            if (i + 1 < lineas.Count && (lineas[i + 1].ToLowerInvariant().Contains("meses") || lineas[i + 1].ToLowerInvariant().Contains("msi")))
                continue;

            var mp = Regex.Match(lineas[i], @"\$\s*([0-9]{1,3}(?:,[0-9]{3})+|[0-9]{3,5})(?:\.([0-9]{2}))?");
            if (!mp.Success) continue;

            string ent = mp.Groups[1].Value.Replace(",", "");
            string dec = mp.Groups[2].Success ? "." + mp.Groups[2].Value : "";
            if (!decimal.TryParse(ent + dec, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal precio)) continue;
            if (precio < minPrecio || precio > maxPrecio) continue;

            for (int j = i - 1; j >= Math.Max(0, i - 6); j--)
            {
                if (BloqueIndicaAgotado(lineas[j])) break;

                string cand = lineas[j];
                if (CumpleFiltroComponente(cand, terminoCorto))
                {
                    string urlProd = urlFallback;
                    if (!string.IsNullOrWhiteSpace(html) && nombreTienda == "DDTech")
                    {
                        string fragmentoTitulo = cand.Substring(0, Math.Min(18, cand.Length));
                        var mHref = Regex.Match(html, @"href=""(https?://(?:www\.)?ddtech\.mx/producto/[^""]+)""[\s\S]{1,350}?" + Regex.Escape(fragmentoTitulo), RegexOptions.IgnoreCase);
                        if (mHref.Success) urlProd = mHref.Groups[1].Value;
                    }

                    if (!lista.Any(x => x.Titulo.Equals(cand, StringComparison.OrdinalIgnoreCase)))
                        lista.Add((cand, precio, nombreTienda, urlProd));
                    break;
                }
            }
        }

        return lista;
    }

    private (decimal Minimo, decimal Maximo) ObtenerRangoPreciosCoherente(string terminoCorto)
    {
        string q = terminoCorto.ToLowerInvariant();

        if (EsBusquedaDeTarjetaMadre(q))
            return (1200m, 15500m);

        if (q.Contains("ssd") || q.Contains("nvme") || q.Contains("m.2") || q.Contains("estado solido"))
        {
            if (q.Contains("2tb") || q.Contains("4tb")) return (2200m, 13500m);
            if (q.Contains("1tb") || q.Contains("1000gb")) return (1150m, 5800m);
            if (q.Contains("480gb") || q.Contains("500gb") || q.Contains("512gb")) return (750m, 3200m);
            return (500m, 6500m);
        }

        if ((q.Contains("5050") || q.Contains("5060") || q.Contains("4060") || q.Contains("7600") || q.Contains("6600")) && !q.Contains("5070") && !q.Contains("5080") && !q.Contains("ryzen"))
            return (4500m, 16500m);

        if (q.Contains("rtx") || q.Contains("rx ") || q.Contains("radeon") || q.Contains("geforce"))
            return (3800m, 45000m);

        if (q.Contains("9600x") || q.Contains("7600x") || q.Contains("5600") || q.Contains("5700x") || q.Contains("7700x") || q.Contains("9700x"))
            return (1800m, 11500m);

        if (q.Contains("ryzen") || q.Contains("core i") || q.Contains("procesador"))
            return (1500m, 19500m);

        if (q.Contains("ddr5"))
        {
            if (q.Contains("8gb") && !q.Contains("16gb")) return (700m, 3600m);
            if (q.Contains("16gb") && !q.Contains("32gb")) return (1100m, 5200m);
            return (900m, 9500m);
        }

        if (q.Contains("ddr4"))
        {
            if (q.Contains("8gb") && !q.Contains("16gb")) return (380m, 2400m);
            if (q.Contains("16gb") && !q.Contains("32gb")) return (650m, 3900m);
            return (380m, 5500m);
        }

        return (400m, 35000m);
    }

    private bool CumpleFiltroComponente(string titulo, string terminoCorto)
    {
        if (string.IsNullOrWhiteSpace(titulo) || titulo.Length < 12 || titulo.Length > 250) return false;
        if (EsPublicacionDeSegundaMano(titulo)) return false;   // SOLO PRODUCTO NUEVO

        string tLower = Regex.Replace(titulo.ToLowerInvariant(), @"(\d+)\s+(gb|tb|mhz|mt)", "$1$2");
        tLower = Regex.Replace(tLower, @"ddr\s+([345])", "ddr$1");
        tLower = Regex.Replace(tLower, @"so-?dimm", "sodimm");
        tLower = Regex.Replace(tLower, @"u-?dimm", "udimm");

        string[] frasesBasuraYAccesorios =
        {
            "resultados para", "resultado para", "1 a 48 de", "1-48 de", "1 a 24 de", "1-24 de",
            "mostrando ", "familia de procesadores", "procesador gráfico,", "procesador grafico,",
            "frecuencia del procesador", "tipo de memoria", "ancho de datos", " máxima ",
            "búsquedas relacionadas", "busquedas relacionadas", "clientes también", "omitir e ir",
            "filtros", "ordenar por", "envío gratis", "agregar al carrito", "comprar ahora",
            "consulta la página", "el precio y otros detalles", "agotado", "sin existencia", "sin stock",
            "precio club", "precio web", "con existencia",
            "caja de disco", "carcasa", "enclosure", "cofre", "case para", "adaptador",
            "lector de", "xbox", "playstation", "ps5", "funda", "estuche", "disipador para ssd",
            "cable ", "convertidor", "soporte para"
        };

        if (frasesBasuraYAccesorios.Any(b => tLower.Contains(b))) return false;
        if (titulo.Count(c => c == ';') >= 1) return false;

        string qLower = terminoCorto.ToLowerInvariant().Replace("so-dimm", "sodimm");
        bool pideRamSodimm = _modoLaptopActivo || qLower.Contains("sodimm") || qLower.Contains("laptop");
        bool esBusquedaRam = qLower.Contains("ddr4") || qLower.Contains("ddr5") || qLower.Contains("ddr3");
        bool esBusquedaSsd = qLower.Contains("ssd") || qLower.Contains("nvme") || qLower.Contains("m.2");

        if (esBusquedaRam && pideRamSodimm)
        {
            if (tLower.Contains("udimm")) return false;
            if (!tLower.Contains("sodimm") && !tLower.Contains("laptop") && !tLower.Contains("notebook") && !tLower.Contains("portátil") && !tLower.Contains("portatil"))
                return false;
        }
        else if (esBusquedaRam && !pideRamSodimm)
        {
            if (tLower.Contains("sodimm") || tLower.Contains("para laptop") || tLower.Contains("para notebook"))
                return false;
        }

        string tParaMatch = tLower;
        if (tParaMatch.Contains("estado solido") || tParaMatch.Contains("estado sólido") || tParaMatch.Contains("nvme"))
        {
            tParaMatch += " ssd";
        }
        string tSinEspaciosMatch = tParaMatch.Replace(" ", "");

        string[] palabrasClave = qLower.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                       .Where(p => p != "sodimm" && p != "laptop" && p != "interno")
                                       .ToArray();

        if (!palabrasClave.All(p => tParaMatch.Contains(p) || tSinEspaciosMatch.Contains(p))) return false;

        if (qLower.Contains("8gb") && !qLower.Contains("16gb"))
        {
            if (tLower.Contains("16gb") || tLower.Contains("32gb") || Regex.IsMatch(tLower, @"2\s*x\s*8gb"))
                return false;
        }
        else if (qLower.Contains("16gb") && !qLower.Contains("32gb"))
        {
            if (tLower.Contains("32gb") || tLower.Contains("64gb") || Regex.IsMatch(tLower, @"2\s*x\s*16gb"))
                return false;
        }

        bool buscaMother = EsBusquedaDeTarjetaMadre(qLower);

        string[] excluidasEquiposArmados = { "computadora", "ultimate pc", "pc gamer", "pride ", "all in one", "workstation", "ensamble", "desktop", "xtreme pc", "kit de actualizacion", "kit actualización" };
        if (excluidasEquiposArmados.Any(excl => tLower.Contains(excl))) return false;

        if (!esBusquedaRam && !esBusquedaSsd)
        {
            if (tLower.Contains("laptop ") || tLower.Contains("notebook ") || tLower.Contains("portátil "))
                return false;
        }

        if (buscaMother)
        {
            if (tLower.StartsWith("procesador") || tLower.StartsWith("amd procesador") || tLower.StartsWith("intel procesador") || tLower.StartsWith("memoria"))
                return false;
        }
        else
        {
            bool empiezaComoOtraPieza = tLower.StartsWith("procesador") ||
                                        tLower.StartsWith("amd procesador") ||
                                        tLower.StartsWith("intel procesador") ||
                                        tLower.StartsWith("memoria") ||
                                        tLower.StartsWith("unidad de estado") ||
                                        tLower.StartsWith("ssd") ||
                                        tLower.StartsWith("tarjeta de video");

            if (!empiezaComoOtraPieza)
            {
                string tSinAdvertenciaBios = tLower.Replace("actualizar tarjeta madre", "").Replace("compatible con tarjeta madre", "");
                string[] excluidasMother = { "tarjeta madre", "motherboard", "placa base", "placa madre", "micro-atx", "micro atx", "mini-itx", "b650", "b850", "b550", "a520", "a620", "x670", "x870", "z790", "b760", "h610" };
                if (excluidasMother.Any(excl => tSinAdvertenciaBios.Contains(excl)))
                    return false;
            }
        }

        return true;
    }

    private void OnSeleccionarResultadoApiClicked(object? sender, EventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is OpcionMercadoItem elegido)
        {
            string tituloLimpio = elegido.Titulo;
            int idxRequiere = tituloLimpio.IndexOf("/ Requiere", StringComparison.OrdinalIgnoreCase);
            if (idxRequiere > 0)
                tituloLimpio = tituloLimpio.Substring(0, idxRequiere).Trim();

            var partes = tituloLimpio.Split('/').Select(p => p.Trim()).Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (partes.Count > 3)
                tituloLimpio = string.Join(" / ", partes.Take(3));

            txtDescripcion.Text = tituloLimpio;
            txtCostoInterno.Text = Math.Ceiling(elegido.Precio).ToString("0");
            panelResultadosApi.IsVisible = false;
            lblEstadoApi.Text = $"Cargado: {tituloLimpio} (${elegido.Precio:N2}) -> Precio Cliente (+12%): ${txtPrecio.Text} MXN";
        }
    }

    private async void OnVerProductoClicked(object? sender, EventArgs e)
    {
        if (sender is not Button btn || btn.CommandParameter is not OpcionMercadoItem it) return;

        if (!string.IsNullOrWhiteSpace(it.UrlProducto))
        {
            await AbrirUrlEnNavegadorAsync(it.UrlProducto);
            return;
        }

        switch (it.NombreTienda)
        {
            case "Zegucom": OnAbrirZegucomClicked(sender, e); break;
            case "DDTech": OnAbrirDdtechClicked(sender, e); break;
            case "Amazon MX": OnAbrirAmazonClicked(sender, e); break;
            case "Mercado Libre": OnAbrirMercadoLibreClicked(sender, e); break;
            case "Cyberpuerta": OnAbrirCyberpuertaClicked(sender, e); break;
        }
    }

    private void OnCerrarPanelApiClicked(object? sender, EventArgs e)
    {
        panelResultadosApi.IsVisible = false;
    }

    private async void OnAbrirZegucomClicked(object? sender, EventArgs e)
    {
        var (_, _, qZegucom, _) = PrepararBusquedas(txtBuscarApi.Text ?? "");
        if (string.IsNullOrWhiteSpace(txtDescripcion.Text))
            txtDescripcion.Text = qZegucom;

        string url = !string.IsNullOrWhiteSpace(_urlGanadorZegucom)
            ? _urlGanadorZegucom
            : ConstruirUrlRealZegucom(qZegucom);

        await AbrirUrlEnNavegadorAsync(url);
        txtCostoInterno.Focus();
    }

    private async void OnAbrirDdtechClicked(object? sender, EventArgs e)
    {
        var (_, qDdtech, _, _) = PrepararBusquedas(txtBuscarApi.Text ?? "");
        if (string.IsNullOrWhiteSpace(txtDescripcion.Text))
            txtDescripcion.Text = qDdtech;

        string url = !string.IsNullOrWhiteSpace(_urlGanadorDdtech)
            ? _urlGanadorDdtech
            : $"https://ddtech.mx/buscar/{Uri.EscapeDataString(qDdtech).Replace("%20", "+")}";

        await AbrirUrlEnNavegadorAsync(url);
        txtCostoInterno.Focus();
    }

    private async void OnAbrirAmazonClicked(object? sender, EventArgs e)
    {
        var (_, _, _, qAmazon) = PrepararBusquedas(txtBuscarApi.Text ?? "");
        if (string.IsNullOrWhiteSpace(txtDescripcion.Text))
            txtDescripcion.Text = qAmazon;

        string url = !string.IsNullOrWhiteSpace(_urlGanadorAmazon)
            ? _urlGanadorAmazon
            : $"https://www.amazon.com.mx/s?k={Uri.EscapeDataString(qAmazon)}";

        await AbrirUrlEnNavegadorAsync(url);
        txtCostoInterno.Focus();
    }

    private async void OnAbrirMercadoLibreClicked(object? sender, EventArgs e)
    {
        var (_, _, _, qAmazon) = PrepararBusquedas(txtBuscarApi.Text ?? "");
        if (string.IsNullOrWhiteSpace(txtDescripcion.Text))
            txtDescripcion.Text = qAmazon;

        string url = !string.IsNullOrWhiteSpace(_urlGanadorMercadoLibre)
            ? _urlGanadorMercadoLibre
            : ConstruirUrlMercadoLibre(qAmazon);

        await AbrirUrlEnNavegadorAsync(url);
        txtCostoInterno.Focus();
    }

    private async void OnAbrirCyberpuertaClicked(object? sender, EventArgs e)
    {
        var (_, _, qZegucom, _) = PrepararBusquedas(txtBuscarApi.Text ?? "");
        if (string.IsNullOrWhiteSpace(txtDescripcion.Text))
            txtDescripcion.Text = qZegucom;

        string url = !string.IsNullOrWhiteSpace(_urlGanadorCyberpuerta)
            ? _urlGanadorCyberpuerta
            : ConstruirUrlCyberpuerta(qZegucom);

        await AbrirUrlEnNavegadorAsync(url);
        txtCostoInterno.Focus();
    }

    private async void OnAgregarItemClicked(object? sender, EventArgs e)
    {
        string desc = txtDescripcion.Text?.Trim() ?? "";
        string precioLimpio = LimpiarTextoDecimal(txtPrecio.Text ?? "");
        string costoLimpio = LimpiarTextoDecimal(txtCostoInterno.Text ?? "0");
        string cantidadLimpia = txtCantidad.Text?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(costoLimpio)) costoLimpio = "0";

        if (string.IsNullOrWhiteSpace(desc) || desc.Length < 3 || !desc.Any(char.IsLetter))
        {
            await DisplayAlertAsync("Descripción inválida", "Escribe una descripción válida que contenga letras (mínimo 3 caracteres).", "OK");
            return;
        }

        if (!int.TryParse(cantidadLimpia, out int cant) || cant <= 0 || cant > 99)
        {
            await DisplayAlertAsync("Cantidad inválida", "La cantidad debe ser un número entero entre 1 y 99.", "OK");
            txtCantidad.Text = "1";
            return;
        }

        if (!decimal.TryParse(precioLimpio, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal precio) || precio <= 0)
        {
            await DisplayAlertAsync("Precio inválido", "Ingresa un precio al cliente mayor a $0.", "OK");
            return;
        }

        decimal.TryParse(costoLimpio, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal costo);

        if (costo > precio)
        {
            bool continuarPerdida = await DisplayAlertAsync("Advertencia de Costo",
                $"El Precio al Cliente (${precio:N2}) es MENOR que tu Costo Proveedor (${costo:N2}). ¿Seguro que deseas agregarlo con pérdida?",
                "Sí, agregar", "Corregir precio");
            if (!continuarPerdida) return;
        }

        int pos = Conceptos.Count > 0 && Conceptos.Last().EsManoDeObra ? Conceptos.Count - 1 : Conceptos.Count;

        Conceptos.Insert(pos, new ItemCotizacion
        {
            Cantidad = cant,
            Descripcion = desc,
            CostoUnitario = costo,
            PrecioUnitario = precio,
            EsManoDeObra = false
        });

        txtDescripcion.Text = string.Empty;
        txtCostoInterno.Text = string.Empty;
        txtPrecio.Text = string.Empty;
        txtBuscarApi.Text = string.Empty;
        txtCantidad.Text = "1";
        panelResultadosApi.IsVisible = false;
        ActualizarTotal();
    }

    private void OnLimpiarListaClicked(object? sender, EventArgs e)
    {
        Conceptos.Clear();
        panelCompatibilidadAuto.IsVisible = false;
        ActualizarTotal();
        lblEstadoBd.Text = string.Empty;
    }

    private void ActualizarTotal()
    {
        decimal totalCliente = Conceptos.Sum(x => x.Total);
        decimal costoTotal = Conceptos.Sum(x => x.CostoTotal);
        decimal gananciaLibre = Conceptos.Sum(x => x.Ganancia);

        lblTotal.Text = $"TOTAL CLIENTE: ${totalCliente:N2} MXN";
        lblCostoTotal.Text = $"Costo interno (piezas + insumos/licencias): ${costoTotal:N2} MXN";
        lblGanancia.Text = $"TU GANANCIA LIBRE: ${gananciaLibre:N2} MXN";
    }

    private async Task GuardarEnBaseDeDatosAsync(CotizacionDto dto)
    {
        try
        {
            lblEstadoBd.Text = "Guardando en Supabase...";

            using var request = new HttpRequestMessage(HttpMethod.Post, SupabaseUrl);
            request.Headers.Add("apikey", SupabaseApiKey);
            request.Headers.Add("Authorization", $"Bearer {SupabaseApiKey}");
            request.Headers.Add("Prefer", "return=minimal");

            request.Content = JsonContent.Create(dto);

            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                lblEstadoBd.Text = "Guardado en la nube (Supabase)";
                if (panelHistorial.IsVisible)
                {
                    await CargarHistorialDesdeSupabaseAsync();
                }
            }
            else
            {
                lblEstadoBd.Text = $"Error API Supabase: {response.StatusCode}";
            }
        }
        catch
        {
            lblEstadoBd.Text = "Sin internet: PDF generado localmente";
        }
    }

    private List<(string Titulo, string Cuerpo)> ObtenerNotasTecnicasSeleccionadas()
    {
        var notas = new List<(string Titulo, string Cuerpo)>();

        if (chkRam.IsChecked)
            notas.Add(("• Ampliación de Memoria RAM (Multitarea y Ancho de Banda)",
                "Elimina cuellos de botella y congelamientos al ejecutar múltiples aplicaciones pesadas o pestañas simultáneamente, permitiendo al equipo trabajar con mayor fluidez."));

        if (chkSsd.IsChecked)
            notas.Add(("• Actualización a Unidad de Estado Sólido (SSD / NVMe)",
                "Incrementa drásticamente la velocidad de lectura/escritura frente a discos tradicionales, reduciendo el tiempo de arranque de Windows a pocos segundos y acelerando la carga de programas."));

        if (chkMantenimiento.IsChecked)
            notas.Add(("• Mantenimiento Térmico e Insumos de Precisión",
                "Incluye limpieza interna de circuito y ventilación con alcohol isopropílico de alta pureza y reemplazo de pasta térmica de alto desempeño (Cooler Master) para reducir temperaturas y evitar pérdida de rendimiento (Thermal Throttling)."));

        if (chkSistema.IsChecked)
            notas.Add(("• Formateo, Optimización de Sistema Operativo y Licenciamiento Oficial",
                "Instalación limpia de Windows con controladores oficiales actualizados, eliminación de archivos basura/virus, activación permanente de licencias (Windows / Office) o migración íntegra de información sin pérdida de datos."));

        if (chkCpuGpu.IsChecked)
            notas.Add(("• Actualización de Procesador (CPU), Placa Madre o Tarjeta Gráfica (GPU)",
                "Incremento directo en la potencia de procesamiento y renderizado gráfico, incluyendo actualización de BIOS, activación de perfiles de memoria (XMP/EXPO) y pruebas de estabilidad."));

        if (chkEnfriamiento.IsChecked)
            notas.Add(("• Sistema de Enfriamiento (AIO / Aire) y Fuente de Alimentación (PSU)",
                "Garantiza un suministro eléctrico estable y certificado para proteger los componentes ante variaciones de voltaje, manteniendo temperaturas óptimas bajo cargas intensas de trabajo o gaming."));

        return notas;
    }

    // =========================================================================================
    // GENERACIÓN UNIFICADA DE PDF (NATIVO EN ANDROID SIN ERRORES / QUESTPDF EN WINDOWS)
    // =========================================================================================
    private async Task<string?> CrearYGuardarPdfInternoAsync()
    {
        if (Conceptos.Count == 0)
        {
            await DisplayAlertAsync("Aviso", "Agrega al menos un servicio o componente a la lista.", "OK");
            return null;
        }

        string telefono = txtTelefono.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(telefono) && telefono.Length != 10)
        {
            await DisplayAlertAsync("Teléfono incompleto", "El número de WhatsApp debe tener exactamente 10 dígitos (o déjalo vacío si solo generarás el PDF).", "OK");
            return null;
        }

        string cliente = string.IsNullOrWhiteSpace(txtCliente.Text) ? "Cliente General" : txtCliente.Text.Trim();
        string equipo = string.IsNullOrWhiteSpace(txtEquipo.Text) ? "Equipo de Cómputo" : txtEquipo.Text.Trim();
        decimal totalGeneral = Conceptos.Sum(x => x.Total);

        await GuardarEnBaseDeDatosAsync(new CotizacionDto
        {
            Cliente = cliente,
            Telefono = telefono,
            Equipo = equipo,
            Fecha = DateTime.UtcNow,
            Total = totalGeneral,
            Detalles = Conceptos.ToList()
        });

        try
        {
            string clienteLimpio = string.Concat(cliente.Split(System.IO.Path.GetInvalidFileNameChars())).Replace(" ", "_");
            string nombreArchivo = $"Cotizacion_{clienteLimpio}_{DateTime.Now:yyyyMMdd_HHmm}.pdf";

            string carpetaDestino = FileSystem.CacheDirectory;
#if WINDOWS
            string misDocumentos = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            carpetaDestino = System.IO.Path.Combine(misDocumentos, "Cotizaciones_PC");
            System.IO.Directory.CreateDirectory(carpetaDestino);
#endif
            string rutaPdf = System.IO.Path.Combine(carpetaDestino, nombreArchivo);
            var notas = ObtenerNotasTecnicasSeleccionadas();

#if ANDROID
            GenerarPdfNativoAndroid(rutaPdf, cliente, equipo, totalGeneral, notas);
#elif WINDOWS
            QuestPDF.Settings.License = LicenseType.Community;
            GenerarPdfConQuestPdfWindows(rutaPdf, cliente, equipo, totalGeneral, notas);
#else
            await DisplayAlertAsync("No soportado", "La generación de PDF solo está disponible en Android y Windows.", "OK");
            return null;
#endif

            return rutaPdf;
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Error al generar PDF", ex.Message, "OK");
            return null;
        }
    }

#if ANDROID
    // PDF nativo de Android con soporte de VARIAS PÁGINAS (la tabla y las notas continúan en la siguiente hoja)
    private void GenerarPdfNativoAndroid(string rutaPdf, string cliente, string equipo, decimal totalGeneral, List<(string Titulo, string Cuerpo)> notas)
    {
        using var document = new Android.Graphics.Pdf.PdfDocument();
        const int pageWidth = 612;  // Tamaño Carta (Letter)
        const int pageHeight = 792;
        const int margin = 36;
        int contentWidth = pageWidth - (margin * 2);
        float limiteY = pageHeight - 44;   // deja espacio para el pie de página

        var paintBg = new Android.Graphics.Paint { AntiAlias = true };
        paintBg.SetStyle(Android.Graphics.Paint.Style.Fill);

        var paintBorder = new Android.Graphics.Paint { AntiAlias = true, StrokeWidth = 1f };
        paintBorder.SetStyle(Android.Graphics.Paint.Style.Stroke);

        var paintTitle = new Android.Graphics.Paint { AntiAlias = true, TextSize = 14f, FakeBoldText = true, Color = Android.Graphics.Color.ParseColor("#D4AF37") };
        var paintSub = new Android.Graphics.Paint { AntiAlias = true, TextSize = 9f, Color = Android.Graphics.Color.ParseColor("#E4E4E7") };
        var paintSec = new Android.Graphics.Paint { AntiAlias = true, TextSize = 11.5f, FakeBoldText = true, Color = Android.Graphics.Color.ParseColor("#18181B") };
        var paintBold = new Android.Graphics.Paint { AntiAlias = true, TextSize = 9.5f, FakeBoldText = true, Color = Android.Graphics.Color.ParseColor("#18181B") };
        var paintReg = new Android.Graphics.Paint { AntiAlias = true, TextSize = 9f, Color = Android.Graphics.Color.ParseColor("#27272A") };
        var paintTh = new Android.Graphics.Paint { AntiAlias = true, TextSize = 9.5f, FakeBoldText = true, Color = Android.Graphics.Color.ParseColor("#D4AF37") };
        var paintFooter = new Android.Graphics.Paint { AntiAlias = true, TextSize = 8f, Color = Android.Graphics.Color.ParseColor("#71717A") };

        Android.Graphics.Pdf.PdfDocument.Page? page = null;
        Android.Graphics.Canvas canvas = null!;
        int numPagina = 0;
        float y = margin;

        // Columnas de la tabla
        float colCantX = margin + 6;
        float colDescX = margin + 44;
        float colPuRight = pageWidth - margin - 88;
        float colSubRight = pageWidth - margin - 8;
        float maxDescWidth = colPuRight - colDescX - 65;

        void DibujarPie()
        {
            string footer = $"Cotización sujeta a disponibilidad y variación del mercado. Garantía incluida en mano de obra, software e instalación.  •  Pág. {numPagina}";
            float fw = paintFooter.MeasureText(footer);
            canvas.DrawText(footer, Math.Max(margin, (pageWidth - fw) / 2f), pageHeight - 20, paintFooter);
        }

        void NuevaPagina()
        {
            if (page != null)
            {
                DibujarPie();
                document.FinishPage(page);
            }
            numPagina++;
            var info = new Android.Graphics.Pdf.PdfDocument.PageInfo.Builder(pageWidth, pageHeight, numPagina).Create();
            page = document.StartPage(info)!;
            canvas = page.Canvas!;
            y = margin;
        }

        void AsegurarEspacio(float alto)
        {
            if (y + alto > limiteY) NuevaPagina();
        }

        void DibujarEncabezadoTabla()
        {
            paintBg.Color = Android.Graphics.Color.ParseColor("#18181B");
            canvas.DrawRect(margin, y, pageWidth - margin, y + 22, paintBg);
            canvas.DrawText("Cant.", colCantX, y + 15, paintTh);
            canvas.DrawText("Descripción", colDescX, y + 15, paintTh);
            DibujarTextoDerechaAndroid(canvas, "P. Unitario", colPuRight, y + 15, paintTh);
            DibujarTextoDerechaAndroid(canvas, "Subtotal", colSubRight, y + 15, paintTh);
            y += 22;
        }

        NuevaPagina();

        // 1. Encabezado Black & Gold
        paintBg.Color = Android.Graphics.Color.ParseColor("#18181B");
        canvas.DrawRect(margin, y, pageWidth - margin, y + 52, paintBg);
        canvas.DrawText("SOPORTE TÉCNICO, SOFTWARE Y ENSAMBLE PROFESIONAL", margin + 12, y + 23, paintTitle);
        canvas.DrawText("Mantenimiento Térmico, Formateo, Licenciamiento y Actualización de Hardware (PC y Laptop)", margin + 12, y + 40, paintSub);
        y += 64;

        // 2. Caja de Datos del Cliente
        paintBg.Color = Android.Graphics.Color.ParseColor("#FAFAFA");
        paintBorder.Color = Android.Graphics.Color.ParseColor("#E4E4E7");
        canvas.DrawRect(margin, y, pageWidth - margin, y + 42, paintBg);
        canvas.DrawRect(margin, y, pageWidth - margin, y + 42, paintBorder);

        canvas.DrawText($"Fecha: {DateTime.Now:dd/MM/yyyy}", margin + 10, y + 16, paintReg);
        canvas.DrawText($"Cliente: {cliente}", margin + 10, y + 32, paintBold);

        float midX = margin + (contentWidth * 0.50f);
        canvas.DrawText("Validez: 15 días naturales", midX, y + 16, paintReg);
        var lineasEq = DividirTextoEnLineasAndroid($"Equipo(s): {equipo}", paintBold, (pageWidth - margin) - midX - 8);
        canvas.DrawText(lineasEq.FirstOrDefault() ?? "", midX, y + 32, paintBold);
        y += 56;

        // 3. Tabla de Conceptos
        canvas.DrawText("Desglose de Servicios, Licencias y Componentes", margin, y, paintSec);
        y += 8;

        DibujarEncabezadoTabla();

        paintBorder.Color = Android.Graphics.Color.ParseColor("#E4E4E7");
        foreach (var item in Conceptos)
        {
            var lineasDesc = DividirTextoEnLineasAndroid(item.Descripcion, paintReg, maxDescWidth);
            float rowH = Math.Max(20f, lineasDesc.Count * 12f + 8f);

            if (y + rowH > limiteY)
            {
                NuevaPagina();
                DibujarEncabezadoTabla();
                paintBorder.Color = Android.Graphics.Color.ParseColor("#E4E4E7");
            }

            canvas.DrawText(item.Cantidad.ToString(), colCantX + 4, y + 14, paintReg);
            float ty = y + 14;
            foreach (var l in lineasDesc)
            {
                canvas.DrawText(l, colDescX, ty, paintReg);
                ty += 12f;
            }
            DibujarTextoDerechaAndroid(canvas, $"${item.PrecioUnitario:N2}", colPuRight, y + 14, paintReg);
            DibujarTextoDerechaAndroid(canvas, $"${item.Total:N2}", colSubRight, y + 14, paintBold);

            y += rowH;
            canvas.DrawLine(margin, y, pageWidth - margin, y, paintBorder);
        }

        // Fila Total Neto
        AsegurarEspacio(40);
        paintBg.Color = Android.Graphics.Color.ParseColor("#18181B");
        canvas.DrawRect(margin, y, pageWidth - margin, y + 26, paintBg);
        DibujarTextoDerechaAndroid(canvas, "TOTAL NETO:", colPuRight, y + 17, paintTh);
        DibujarTextoDerechaAndroid(canvas, $"${totalGeneral:N2} MXN", colSubRight, y + 17, paintTh);
        y += 40;

        // 4. Justificaciones Técnicas (continúan en otra página si no caben)
        if (notas.Count > 0)
        {
            AsegurarEspacio(60);
            canvas.DrawText("Justificación Técnica del Servicio", margin, y, paintSec);
            y += 8;

            foreach (var nota in notas)
            {
                var lineasCuerpo = DividirTextoEnLineasAndroid(nota.Cuerpo, paintReg, contentWidth - 16);
                float boxH = 22f + (lineasCuerpo.Count * 11.5f) + 6f;

                AsegurarEspacio(boxH + 6f);

                paintBg.Color = Android.Graphics.Color.ParseColor("#FAFAFA");
                paintBorder.Color = Android.Graphics.Color.ParseColor("#D4AF37");
                canvas.DrawRect(margin, y, pageWidth - margin, y + boxH, paintBg);
                canvas.DrawRect(margin, y, pageWidth - margin, y + boxH, paintBorder);

                canvas.DrawText(nota.Titulo, margin + 8, y + 14, paintBold);
                float ny = y + 27;
                foreach (var lc in lineasCuerpo)
                {
                    canvas.DrawText(lc, margin + 8, ny, paintReg);
                    ny += 11.5f;
                }
                y += boxH + 6f;
            }
        }

        // Cierra la última página
        DibujarPie();
        document.FinishPage(page!);

        using var fs = new System.IO.FileStream(rutaPdf, System.IO.FileMode.Create, System.IO.FileAccess.Write);
        document.WriteTo(fs);
    }

    private void DibujarTextoDerechaAndroid(Android.Graphics.Canvas canvas, string texto, float xDerecha, float y, Android.Graphics.Paint paint)
    {
        float ancho = paint.MeasureText(texto);
        canvas.DrawText(texto, xDerecha - ancho, y, paint);
    }

    private List<string> DividirTextoEnLineasAndroid(string texto, Android.Graphics.Paint paint, float maxAncho)
    {
        var lineas = new List<string>();
        if (string.IsNullOrWhiteSpace(texto)) return lineas;

        var palabras = texto.Split(' ');
        string actual = "";

        foreach (var p in palabras)
        {
            string prueba = string.IsNullOrEmpty(actual) ? p : $"{actual} {p}";
            if (paint.MeasureText(prueba) <= maxAncho)
            {
                actual = prueba;
            }
            else
            {
                if (!string.IsNullOrEmpty(actual)) lineas.Add(actual);
                actual = p;
            }
        }
        if (!string.IsNullOrEmpty(actual)) lineas.Add(actual);
        return lineas;
    }
#endif

#if WINDOWS
    private void GenerarPdfConQuestPdfWindows(string rutaPdf, string cliente, string equipo, decimal totalGeneral, List<(string Titulo, string Cuerpo)> notas)
    {
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(1.5f, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Header().Background("#18181B").Padding(15).Column(col =>
                {
                    col.Item().Text("SOPORTE TÉCNICO, SOFTWARE Y ENSAMBLE PROFESIONAL").FontSize(17).Bold().FontColor("#D4AF37");
                    col.Item().Text("Mantenimiento Térmico, Formateo, Licenciamiento y Actualización de Hardware (PC y Laptop)").FontSize(10).FontColor("#E4E4E7");
                });

                page.Content().PaddingVertical(15).Column(col =>
                {
                    col.Spacing(10);

                    col.Item().Border(1).BorderColor("#E4E4E7").Background("#FAFAFA").Padding(10).Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text($"Fecha: {DateTime.Now:dd/MM/yyyy}");
                            c.Item().Text($"Cliente: {cliente}").Bold();
                        });
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text("Validez: 15 días naturales");
                            c.Item().Text($"Equipo(s): {equipo}").Bold();
                        });
                    });

                    col.Item().Text("Desglose de Servicios, Licencias y Componentes").FontSize(13).Bold().FontColor("#18181B");

                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.ConstantColumn(45);
                            columns.RelativeColumn();
                            columns.ConstantColumn(85);
                            columns.ConstantColumn(85);
                        });

                        table.Header(header =>
                        {
                            header.Cell().Background("#18181B").Padding(6).Text("Cant.").FontColor("#D4AF37").Bold();
                            header.Cell().Background("#18181B").Padding(6).Text("Descripción").FontColor("#D4AF37").Bold();
                            header.Cell().Background("#18181B").Padding(6).AlignRight().Text("P. Unitario").FontColor("#D4AF37").Bold();
                            header.Cell().Background("#18181B").Padding(6).AlignRight().Text("Subtotal").FontColor("#D4AF37").Bold();
                        });

                        foreach (var item in Conceptos)
                        {
                            table.Cell().BorderBottom(1).BorderColor("#E4E4E7").Padding(6).Text(item.Cantidad.ToString());
                            table.Cell().BorderBottom(1).BorderColor("#E4E4E7").Padding(6).Text(item.Descripcion);
                            table.Cell().BorderBottom(1).BorderColor("#E4E4E7").Padding(6).AlignRight().Text($"${item.PrecioUnitario:N2}");
                            table.Cell().BorderBottom(1).BorderColor("#E4E4E7").Padding(6).AlignRight().Text($"${item.Total:N2}");
                        }

                        table.Cell().ColumnSpan(3).Background("#18181B").Padding(8).AlignRight().Text("TOTAL NETO:").Bold().FontColor("#D4AF37");
                        table.Cell().Background("#18181B").Padding(8).AlignRight().Text($"${totalGeneral:N2} MXN").Bold().FontColor("#D4AF37");
                    });

                    if (notas.Count > 0)
                    {
                        col.Item().PaddingTop(6).Text("Justificación Técnica del Servicio").FontSize(13).Bold().FontColor("#18181B");

                        foreach (var nota in notas)
                        {
                            col.Item().Border(1).BorderColor("#D4AF37").Background("#FAFAFA").Padding(8).Column(c =>
                            {
                                c.Item().Text(nota.Titulo).Bold().FontColor("#18181B");
                                c.Item().Text(nota.Cuerpo);
                            });
                        }
                    }
                });

                page.Footer().AlignCenter().Text("Cotización sujeta a disponibilidad y variación del mercado. Garantía incluida en mano de obra, software e instalación.").FontSize(8).FontColor("#71717A");
            });
        }).GeneratePdf(rutaPdf);
    }

    private void CopiarArchivoPdfAlPortapapelesWindows(string rutaPdf)
    {
        try
        {
            string comandoPs = $"Set-Clipboard -Path '{rutaPdf.Replace("'", "''")}'";
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -STA -WindowStyle Hidden -Command \"{comandoPs}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch { }
    }
#endif

    private async void OnGenerarPdfClicked(object? sender, EventArgs e)
    {
        string? rutaPdf = await CrearYGuardarPdfInternoAsync();
        if (string.IsNullOrEmpty(rutaPdf)) return;

#if WINDOWS
        CopiarArchivoPdfAlPortapapelesWindows(rutaPdf);
        lblEstadoBd.Text = "✅ PDF guardado en Documentos\\Cotizaciones_PC y COPIADO al portapapeles (Pégalo con Ctrl + V).";
        await Launcher.Default.OpenAsync(new OpenFileRequest
        {
            Title = System.IO.Path.GetFileName(rutaPdf),
            File = new ReadOnlyFile(rutaPdf)
        });
#else
        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = "Compartir Cotización PDF",
            File = new ShareFile(rutaPdf)
        });
#endif
    }

    private async void OnEnviarMensajeDirectoClicked(object? sender, EventArgs e)
    {
        string numero = txtTelefono.Text?.Trim() ?? "";
        if (numero.Length != 10 || !numero.All(char.IsDigit))
        {
            await DisplayAlertAsync("WhatsApp inválido", "Ingresa los 10 dígitos exactos del número de celular del cliente arriba.", "OK");
            txtTelefono.Focus();
            return;
        }

        string? rutaPdf = await CrearYGuardarPdfInternoAsync();
        if (string.IsNullOrEmpty(rutaPdf)) return;

        string numeroCompleto = "52" + numero;
        string cliente = string.IsNullOrWhiteSpace(txtCliente.Text) ? "" : txtCliente.Text.Trim();
        string equipo = string.IsNullOrWhiteSpace(txtEquipo.Text) ? "tu equipo" : txtEquipo.Text.Trim();
        decimal total = Conceptos.Sum(x => x.Total);

        string msg = $"Hola {cliente}, te comparto la cotización para *{equipo}*. " +
                     $"El total es de *${total:N2} MXN*. " +
                     $"Te adjunto el archivo PDF con el desglose completo:";

#if WINDOWS
        CopiarArchivoPdfAlPortapapelesWindows(rutaPdf);
        lblEstadoBd.Text = "📋 ¡PDF generado y copiado! En cuanto abra WhatsApp presiona Ctrl + V para pegar el archivo.";

        string url = $"https://wa.me/{numeroCompleto}?text={Uri.EscapeDataString(msg)}";
        await AbrirUrlEnNavegadorAsync(url);
#else
        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = $"Enviar cotización a {cliente}",
            File = new ShareFile(rutaPdf)
        });
#endif
    }
}
