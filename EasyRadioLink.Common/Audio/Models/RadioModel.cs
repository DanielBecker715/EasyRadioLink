using EasyRadioLink.Common.Audio.Dsp;
using EasyRadioLink.Common.Audio.Models.Dto;
using EasyRadioLink.Common.Audio.Providers;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models.Player;
using NAudio.Utils;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NLog;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace EasyRadioLink.Common.Audio.Models
{
    namespace Dto
    {
        [JsonConverter(typeof(JsonIFilterConverter))]

        internal abstract class IFilter
        {
            public required float Frequency { get; set; }
            public abstract Dsp.IFilter ToFilter();
        };

        internal class FirstOrderLowPassFilter : IFilter
        {
            public override Dsp.IFilter ToFilter()
            {
                return FirstOrderFilter.LowPass(Constants.OUTPUT_SAMPLE_RATE, Frequency);
            }
        }

        internal class FirstOrderHighPassFilter : IFilter
        {
            public override Dsp.IFilter ToFilter()
            {
                return FirstOrderFilter.HighPass(Constants.OUTPUT_SAMPLE_RATE, Frequency);
            }
        }

        internal abstract class IBiQuadFilter : IFilter
        {
            public required float Q { get; set; }
        }

        internal class BiQuadHighPassFilter : IBiQuadFilter
        {
            public override Dsp.IFilter ToFilter()
            {
                return new Dsp.BiQuadFilter
                {
                    Filter = NAudio.Dsp.BiQuadFilter.HighPassFilter(Constants.OUTPUT_SAMPLE_RATE, Frequency, Q)
                };
            }
        };

        

        internal class BiQuadLowPassFilter : IBiQuadFilter
        {
            public override Dsp.IFilter ToFilter()
            {
                return new Dsp.BiQuadFilter
                {
                    Filter = NAudio.Dsp.BiQuadFilter.LowPassFilter(Constants.OUTPUT_SAMPLE_RATE, Frequency, Q)
                };
            }
        };

        internal class BiQuadPeakingEQFilter : IBiQuadFilter
        {
            public required float Gain { get; set; }

            public override Dsp.IFilter ToFilter()
            {
                return new Dsp.BiQuadFilter
                {
                    Filter = NAudio.Dsp.BiQuadFilter.PeakingEQ(Constants.OUTPUT_SAMPLE_RATE, Frequency, Q, Gain)
                };
            }
        };

        [JsonDerivedType(typeof(ChainEffect), typeDiscriminator: "chain")]
        [JsonDerivedType(typeof(FiltersEffect), typeDiscriminator: "filters")]

        [JsonDerivedType(typeof(SaturationEffect), typeDiscriminator: "saturation")]
        [JsonDerivedType(typeof(CompressorEffect), typeDiscriminator: "compressor")]
        [JsonDerivedType(typeof(SidechainCompressorEffect), typeDiscriminator: "sidechainCompressor")]
        [JsonDerivedType(typeof(GainEffect), typeDiscriminator: "gain")]

        [JsonDerivedType(typeof(CVSDEffect), typeDiscriminator: "cvsd")]
        internal abstract class IEffect
        {
            public abstract ISampleProvider ToSampleProvider(ISampleProvider source);
        };

        internal class CVSDEffect : IEffect
        {
            public override ISampleProvider ToSampleProvider(ISampleProvider source)
            {
                return new CVSDProvider(source);
            }
        };

        internal class ChainEffect : IEffect
        {
            public required IEffect[] Effects { get; set; }
            public override ISampleProvider ToSampleProvider(ISampleProvider source)
            {
                var last = source;
                foreach (var effect in Effects)
                {
                    last = effect.ToSampleProvider(last);
                }
                return last;
            }
        };

        internal class FiltersEffect : IEffect
        {
            public required IFilter[] Filters { get; set; }
            public override ISampleProvider ToSampleProvider(ISampleProvider source)
            {
                var filters = new Dsp.IFilter[Filters.Length];
                for (var i = 0; i < Filters.Length; ++i)
                {
                    filters[i] = Filters[i].ToFilter();
                }
                return new FiltersProvider(source)
                {
                    Filters = filters
                };
            }
        };

        internal class SaturationEffect : IEffect
        {
            public required float Gain { get; set; }
            public required float Threshold { get; set; }

            public override ISampleProvider ToSampleProvider(ISampleProvider source)
            {
                return new SaturationProvider(source)
                {
                    GainDB = Gain,
                    ThresholdDB = Threshold
                };
            }
        };

        internal class CompressorEffect : IEffect
        {
            public required float Attack { get; set; }
            public required float MakeUp { get; set; }
            public required float Release { get; set; }
            public required float Threshold { get; set; }
            public required float Ratio { get; set; }

            public override ISampleProvider ToSampleProvider(ISampleProvider source)
            {
                // self-keyed: the sidechain is the signal itself (NoopSampleProvider leaves the copied buffer as it is)
                return new SidechainCompressorProvider
                {
                    Compressor = new Dsp.SidechainCompressor(Attack * 1000, Release * 1000, source.WaveFormat.SampleRate)
                    {
                        MakeUpGain = MakeUp,
                        Threshold = Threshold,
                        Ratio = Ratio,
                    },
                    SignalProvider = source,
                    SidechainProvider = new NoopSampleProvider { WaveFormat = source.WaveFormat }
                };
            }
        };

        internal class SidechainCompressorEffect : IEffect
        {
            public required float Attack { get; set; }
            public required float MakeUp { get; set; }
            public required float Release { get; set; }
            public required float Threshold { get; set; }
            public required float Ratio { get; set; }
            public required IEffect SidechainEffect {  get; set; }

            public override ISampleProvider ToSampleProvider(ISampleProvider source)
            {
                return new SidechainCompressorProvider
                {
                    Compressor = new Dsp.SidechainCompressor(Attack * 1000, Release * 1000, source.WaveFormat.SampleRate)
                    {
                        MakeUpGain = MakeUp,
                        Threshold = Threshold,
                        Ratio = Ratio,
                    },
                    SignalProvider = source,
                    SidechainProvider = SidechainEffect.ToSampleProvider(new NoopSampleProvider()
                    {
                        WaveFormat = source.WaveFormat
                    })
                };
            }
        }

        internal class GainEffect : IEffect
        {
            public required float Gain {  get; set; }

            public override ISampleProvider ToSampleProvider(ISampleProvider source)
            {
                return new VolumeSampleProvider(source)
                {
                    Volume = (float)Decibels.DecibelsToLinear(Gain),
                };
            }
        };

        internal class RadioModel
        {
            public required int Version { get; set; }
            public required float NoiseGain { get; set; }
            public required IEffect TxEffect { get; set; }
            public required IEffect RxEffect { get; set; }
            public IEffect EncryptionEffect { get; set; }

            // Optional metadata for the UI (ignored by the audio pipeline).
            public string DisplayName { get; set; }
            public string Description { get; set; }
            public int? SortOrder { get; set; }
        };
    }


    internal class TxRadioModel
    {
        public DeferredSourceProvider TxSource { get; } = new DeferredSourceProvider();

        public ISampleProvider TxEffectProvider { get; init; }

        public ISampleProvider EncryptionProvider { get; init; }

        public float NoiseGain { get; init; }

        public TxRadioModel(Models.Dto.RadioModel dtoPreset)
        {
            TxEffectProvider = dtoPreset.TxEffect.ToSampleProvider(TxSource);

            if (dtoPreset.EncryptionEffect != null)
            {
                EncryptionProvider = dtoPreset.EncryptionEffect.ToSampleProvider(TxEffectProvider);
            }

            NoiseGain = dtoPreset.NoiseGain;
        }
    }

    internal class RxRadioModel
    {
        public DeferredSourceProvider RxSource { get; } = new DeferredSourceProvider();

        public ISampleProvider RxEffectProvider { get; init; }

        public RxRadioModel(Models.Dto.RadioModel dtoPreset)
        {
            RxEffectProvider = dtoPreset.RxEffect.ToSampleProvider(RxSource);
        }
    }

    /// <summary>A radio model ("Sound") a radio can use, for the UI.</summary>
    public sealed class RadioModelInfo
    {
        internal RadioModelInfo(string key, string displayName, string description, int sortOrder, bool isCustom,
            string filePath)
        {
            Key = key;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? key : displayName.Trim();
            Description = description?.Trim() ?? string.Empty;
            SortOrder = sortOrder;
            IsCustom = isCustom;
            FilePath = filePath;
        }

        /// <summary>Model key = file name without extension, e.g. "cb". This is what <c>radio.model</c> stores.</summary>
        public string Key { get; }

        /// <summary>Name shown in the UI, e.g. "CB Radio 27 MHz" (falls back to the key).</summary>
        public string DisplayName { get; }

        /// <summary>One-line description (may be empty).</summary>
        public string Description { get; }

        public int SortOrder { get; }

        /// <summary>True if the model was loaded from the user folder (<see cref="RadioModelFactory.CustomModelsFolder" />).</summary>
        public bool IsCustom { get; }

        /// <summary>Source file, or null for a model that is built into the code.</summary>
        public string FilePath { get; }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    /// <summary>
    ///     Loads the radio models ("sounds") from <c>&lt;ProgramDirectory&gt;\RadioModels\*.json</c> (built-in) and
    ///     <see cref="AppPaths.CustomRadioModelsDirectory" /> (user overrides; a file with the same key replaces the
    ///     built-in one). The key of a model is its file name without extension, normalised like
    ///     <see cref="RadioBase.Model" /> (lower case letters and digits, max 32 characters).
    ///     Files are read once at start-up - restart the application after adding a model.
    /// </summary>
    public class RadioModelFactory
    {
        public const string ModelsFolderName = "RadioModels";

        /// <summary>Default model and fallback for unknown/missing model names.</summary>
        public const string DefaultModelKey = "standard";

        /// <summary>Clean model used by <see cref="Modulation.DIGITAL" /> radios when the sender's model is unknown.</summary>
        public const string DigitalModelKey = "digital";

        /// <summary>Internal model that shapes the HF static - never offered to the user.</summary>
        public const string HfNoiseModelKey = "hfnoise";

        private const int MaxModelKeyLength = 32;
        private const int DefaultSortOrder = 1000;

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private static readonly Lazy<RadioModelFactory> _instance = new(
            () => new RadioModelFactory(new[] { BuiltInModelsFolder, CustomModelsFolder }, 1),
            LazyThreadSafetyMode.ExecutionAndPublication);

        private static readonly JsonSerializerOptions DeserializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase, // "propertyName" (starts lowercase)
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip, // Allow comments but ignore them.
            AllowOutOfOrderMetadataProperties = true, // "$type" does not have to be the first property
        };

        private readonly IReadOnlyDictionary<string, RadioModel> _templates;
        private readonly IReadOnlyDictionary<string, RadioModelInfo> _infos;

        /// <param name="folders">Folders to scan (non-recursive), later folders override earlier ones.</param>
        /// <param name="firstCustomFolderIndex">Index of the first folder whose models count as custom.</param>
        private RadioModelFactory(IReadOnlyList<string> folders, int firstCustomFolderIndex)
        {
            var templates = new Dictionary<string, RadioModel>();
            var infos = new Dictionary<string, RadioModelInfo>();
            var errors = new List<string>();

            for (var i = 0; i < folders.Count; i++)
            {
                LoadFolder(folders[i], i >= firstCustomFolderIndex, templates, infos, errors);
            }

            // Code-built defaults are always available, even when their files are missing.
            if (!templates.ContainsKey(DefaultModelKey))
            {
                templates[DefaultModelKey] = DefaultRadioModels.BuildStandard();
                infos[DefaultModelKey] = new RadioModelInfo(DefaultModelKey, "Standard",
                    "Default radio sound.", 0, false, null);
            }

            if (!templates.ContainsKey(DigitalModelKey))
            {
                templates[DigitalModelKey] = DefaultRadioModels.BuildDigital();
                infos[DigitalModelKey] = new RadioModelInfo(DigitalModelKey, "Digital / Clean",
                    "Clear, wide-band digital voice.", 800, false, null);
            }

            _templates = templates.ToFrozenDictionary();
            _infos = infos.ToFrozenDictionary();
            LoadErrors = errors;

            AvailableModels = infos.Values
                .Where(info => info.Key != HfNoiseModelKey)
                .OrderBy(info => info.Key == DefaultModelKey ? 0 : 1)
                .ThenBy(info => info.SortOrder)
                .ThenBy(info => info.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Logger.Info($"Loaded radio models: {string.Join(", ", _templates.Keys.Order(StringComparer.Ordinal))}");
        }

        /// <summary>The application-wide factory (built-in folder + user folder).</summary>
        public static RadioModelFactory Instance => _instance.Value;

        /// <summary><c>&lt;ProgramDirectory&gt;\RadioModels</c>.</summary>
        public static string BuiltInModelsFolder => Path.Combine(AppPaths.ProgramDirectory, ModelsFolderName);

        /// <summary><c>%AppData%\EasyRadioLink\RadioModels</c> - user models, override built-in models with the same key.</summary>
        public static string CustomModelsFolder => AppPaths.CustomRadioModelsDirectory;

        /// <summary>
        ///     Models the user can pick (key + display name), "standard" first, then by <c>sortOrder</c> and display name.
        ///     The internal "hfnoise" model is excluded.
        /// </summary>
        public IReadOnlyList<RadioModelInfo> AvailableModels { get; }

        /// <summary>Files that could not be loaded ("path: reason"). Empty when everything loaded.</summary>
        public IReadOnlyList<string> LoadErrors { get; }

        /// <summary>
        ///     Creates a factory that only reads the given folders (later folders override earlier ones; none of them
        ///     counts as custom). Intended for tools and tests - the application uses <see cref="Instance" />.
        /// </summary>
        public static RadioModelFactory FromFolders(params string[] folders)
        {
            return new RadioModelFactory(folders ?? Array.Empty<string>(), int.MaxValue);
        }

        /// <summary>Same normalisation as <see cref="RadioBase.Model" />: lower case, letters and digits only, max 32.</summary>
        public static string NormaliseModelKey(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;

            var key = new string(name.Trim().ToLowerInvariant().Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9')
                .ToArray());

            return key.Length > MaxModelKeyLength ? key.Substring(0, MaxModelKeyLength) : key;
        }

        /// <summary>True if a model with this key (after normalisation) exists.</summary>
        public bool HasModel(string key)
        {
            return _templates.ContainsKey(NormaliseModelKey(key));
        }

        /// <summary>UI info of a model, or null if unknown.</summary>
        public RadioModelInfo GetModelInfo(string key)
        {
            return _infos.TryGetValue(NormaliseModelKey(key), out var info) ? info : null;
        }

        /// <summary>
        ///     The key that will actually be used for <paramref name="requested" />: the normalised key if the model exists,
        ///     otherwise <paramref name="fallbackKey" /> (default "standard").
        /// </summary>
        public string ResolveModelKey(string requested, string fallbackKey = DefaultModelKey)
        {
            var key = NormaliseModelKey(requested);
            return key.Length > 0 && _templates.ContainsKey(key) ? key : fallbackKey;
        }

        internal TxRadioModel LoadTxRadio(string name)
        {
            if (name != null && _templates.TryGetValue(name, out var template))
            {
                return new(template);
            }

            return null;
        }

        /// <summary>Sender model, falling back to "standard" (file or code).</summary>
        internal TxRadioModel LoadTxOrDefaultRadio(string name)
        {
            return LoadTxRadio(name) ?? LoadTxRadio(DefaultModelKey) ?? new(DefaultRadioModels.BuildStandard());
        }

        /// <summary>Model for DIGITAL transmissions, falling back to "digital" (file or code).</summary>
        internal TxRadioModel LoadTxOrDefaultDigital(string name)
        {
            return LoadTxRadio(name) ?? LoadTxRadio(DigitalModelKey) ?? new(DefaultRadioModels.BuildDigital());
        }

        internal RxRadioModel LoadRxRadio(string name)
        {
            if (name != null && _templates.TryGetValue(name, out var template))
            {
                return new(template);
            }

            return null;
        }

        /// <summary>Receiving radio model, falling back to "standard" (file or code).</summary>
        internal RxRadioModel LoadRxOrDefault(string name)
        {
            return LoadRxRadio(name) ?? LoadRxRadio(DefaultModelKey) ?? new(DefaultRadioModels.BuildStandard());
        }

        private static void LoadFolder(string folder, bool isCustom, Dictionary<string, RadioModel> templates,
            Dictionary<string, RadioModelInfo> infos, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                if (!isCustom) Logger.Warn($"Radio model folder {folder} is missing");
                return;
            }

            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Unable to list radio model files in {folder}");
                errors.Add($"{folder}: {ex.Message}");
                return;
            }

            foreach (var modelFile in files)
            {
                var key = NormaliseModelKey(Path.GetFileNameWithoutExtension(modelFile));
                if (key.Length == 0)
                {
                    Logger.Error($"Ignoring radio model file {modelFile} - the file name needs letters or digits");
                    errors.Add($"{modelFile}: invalid file name");
                    continue;
                }

                try
                {
                    var template = ReadModelFile(modelFile);

                    templates[key] = template;
                    infos[key] = new RadioModelInfo(key, template.DisplayName, template.Description,
                        template.SortOrder ?? DefaultSortOrder, isCustom, modelFile);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, $"Unable to parse radio model file {modelFile}");
                    errors.Add($"{modelFile}: {ex.Message}");
                }
            }
        }

        private static RadioModel ReadModelFile(string modelFile)
        {
            RadioModel template;
            using (var jsonFile = File.OpenRead(modelFile))
            {
                template = JsonSerializer.Deserialize<RadioModel>(jsonFile, DeserializerOptions);
            }

            if (template?.TxEffect == null || template.RxEffect == null)
                throw new JsonException("txEffect and rxEffect are required");

            // Build the effect chains once so broken files (e.g. missing filter lists) are rejected at load time
            // instead of failing on the audio thread.
            _ = new TxRadioModel(template);
            _ = new RxRadioModel(template);

            return template;
        }
    }

    internal class DefaultRadioModels
    {
        // Default radio FX ("standard" model, identical to RadioModels/standard.json).
        public static Dto.RadioModel BuildStandard() => new()
        {
            Version = 1,
            DisplayName = "Standard",
            TxEffect = new ChainEffect()
            {
                Effects = new IEffect[]
                {
                    new FiltersEffect()
                    {
                        Filters = new Dto.IFilter[]
                        {
                            new BiQuadHighPassFilter
                            {
                                Frequency = 1700,
                                Q = 0.53f
                            },
                            new BiQuadPeakingEQFilter
                            {
                                Frequency = 2801,
                                Q = 0.5f,
                                Gain = 5f
                            },
                            new FirstOrderLowPassFilter
                            {
                                Frequency = 5538
                            }
                        }
                    },

                    new SaturationEffect()
                    {
                        Gain = 9,
                        Threshold = -23,
                    },

                    new SidechainCompressorEffect()
                    {
                        Attack = 0.01f,
                        MakeUp = 6,
                        Release = 0.2f,
                        Threshold = -33,
                        Ratio = 1.18f,
                        SidechainEffect = new FiltersEffect
                        {
                            Filters = new[]
                            {
                                new FirstOrderHighPassFilter
                                {
                                    Frequency = 709
                                }
                            }
                        }
                    },
                    new FiltersEffect()
                    {
                        Filters = new Dto.IFilter[]
                        {
                            new BiQuadHighPassFilter
                            {
                                Frequency = 456,
                                Q = 0.36f
                            },
                            new BiQuadLowPassFilter
                            {
                                Frequency = 5435,
                                Q = 0.39f
                            }
                        }
                    },
                    new GainEffect()
                    {
                        Gain = 10,
                    }

                }

            },

            RxEffect = new FiltersEffect()
            {
                Filters = new Dto.IFilter[]
                {
                    new FirstOrderHighPassFilter
                    {
                        Frequency = 270,
                    },
                    new FirstOrderLowPassFilter
                    {
                        Frequency = 4500
                    }
                },
            },

            EncryptionEffect = new CVSDEffect(),

            NoiseGain = -33,
        };

        // Clean digital voice ("digital" model, identical to RadioModels/digital.json).
        public static Dto.RadioModel BuildDigital() => new()
        {
            Version = 1,
            DisplayName = "Digital / Clean",
            TxEffect = new ChainEffect()
            {
                Effects = new IEffect[]
                {
                    new FiltersEffect()
                    {
                        Filters = new Dto.IFilter[]
                        {
                            new BiQuadHighPassFilter
                            {
                                Frequency = 207,
                                Q = 0.5f
                            },
                            new BiQuadPeakingEQFilter
                            {
                                Frequency = 3112,
                                Q = 0.4f,
                                Gain = 16f
                            },
                            new BiQuadLowPassFilter
                            {
                                Frequency = 6036,
                                Q = 0.4f
                            },
                            new FirstOrderLowPassFilter
                            {
                                Frequency = 5538
                            }
                        },
                    },

                    new SaturationEffect
                    {
                        Gain = 2,
                        Threshold = -33,
                    },

                    new SidechainCompressorEffect
                    {
                        Attack = 0.01f,
                        MakeUp = -1,
                        Release = 0.2f,
                        Threshold = -17,
                        Ratio = 1.18f,
                        SidechainEffect = new FiltersEffect
                        {
                            Filters = new Dto.IFilter[]
                            {
                                new FirstOrderHighPassFilter
                                {
                                    Frequency = 709
                                }
                            }
                        }
                    },

                    new FiltersEffect
                    {
                        Filters = new Dto.IFilter[]
                        {
                            new BiQuadHighPassFilter
                            {
                                Frequency = 393,
                                Q =  0.43f
                            },
                            new BiQuadLowPassFilter
                            {
                                Frequency = 4875,
                                Q = 0.3f
                            }
                        },
                    },

                    new GainEffect
                    {
                        Gain = 10.9f,
                    }
                }
            },

            RxEffect = new FiltersEffect()
            {
                Filters = new Dto.IFilter[]
                {
                    new FirstOrderHighPassFilter
                    {
                        Frequency = 270,
                    },
                    new FirstOrderLowPassFilter
                    {
                        Frequency = 4500
                    }
                },
            },

            EncryptionEffect = new CVSDEffect(),

            NoiseGain = -60,
        };
    };
}
