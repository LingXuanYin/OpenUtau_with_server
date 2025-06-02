using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using OpenUtau.Core;
using OpenUtau.Core.Format;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Render;
using OpenUtau.Core.SignalChain;
using NAudio.Wave;
using OpenUtau.Core.Format.MusicXMLSchema;
using OpenUtau.Core.Util;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using System.Linq;
using OpenUtau.Classic;
using OpenUtau.Api;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace OpenUtau.Core.Controllers {
    [ApiController]
    [Route("api/[controller]")]
    public class ProjectController : ControllerBase {
        private static UProject? currentProject;

        [HttpGet]
        public IActionResult Get() {
            Console.WriteLine("Received GET request for project");
            return Ok(new {
                status = "ok",
                message = "OpenUtau HTTP API is running",
                currentProject = currentProject != null ? new {
                    name = currentProject.name,
                    filePath = currentProject.FilePath
                } : null
            });
        }

        [HttpPost("load")]
        public IActionResult LoadProject([FromBody] LoadProjectRequest request) {
            try {
                UProject? project = null;

                // 检查是否至少提供了一个参数
                if (string.IsNullOrEmpty(request.FilePath) && string.IsNullOrEmpty(request.UstxContent)) {
                    return BadRequest(new { error = "必须提供文件路径或USTX内容中的至少一个" });
                }

                // 如果提供了文件路径
                if (!string.IsNullOrEmpty(request.FilePath)) {
                    if (!System.IO.File.Exists(request.FilePath)) {
                        return NotFound(new { error = "文件不存在" });
                    }
                    project = Formats.ReadProject(new[] { request.FilePath });
                }
                // 如果提供了USTX内容
                else {
                    // 创建临时文件
                    string tempDir = Path.Combine(Path.GetTempPath(), "OpenUtau_Load_" + Guid.NewGuid().ToString());
                    Directory.CreateDirectory(tempDir);
                    string tempFile = Path.Combine(tempDir, "temp.ustx");

                    try {
                        // 保存USTX内容到临时文件
                        System.IO.File.WriteAllText(tempFile, request.UstxContent);
                        project = Formats.ReadProject(new[] { tempFile });
                    }
                    finally {
                        // 清理临时文件
                        try {
                            Directory.Delete(tempDir, true);
                        }
                        catch (Exception ex) {
                            Log.Warning(ex, "清理临时文件失败");
                        }
                    }
                }

                if (project == null) {
                    return BadRequest(new { error = "项目加载失败" });
                }

                // 如果已有项目，先卸载
                if (currentProject != null) {
                    DocManager.Inst.ExecuteCmd(new LoadProjectNotification(new UProject()));
                    // 清除缓存
                    PathManager.Inst.ClearCache();
                }

                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(project));
                currentProject = project;

                return Ok(new {
                    status = "ok",
                    message = "项目加载成功",
                    project = new {
                        name = project.name,
                        filePath = project.FilePath
                    }
                });
            }
            catch (Exception ex) {
                Log.Error(ex, "加载项目时发生错误");
                return StatusCode(500, new { error = "加载项目时发生错误", details = ex.Message });
            }
        }

        [HttpPost("unload")]
        public IActionResult UnloadProject() {
            try {
                if (currentProject == null) {
                    return Ok(new { message = "当前没有加载的项目" });
                }

                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(new UProject()));
                currentProject = null;

                // 清除缓存
                PathManager.Inst.ClearCache();

                return Ok(new { status = "ok", message = "项目已卸载" });
            }
            catch (Exception ex) {
                Log.Error(ex, "卸载项目时发生错误");
                return StatusCode(500, new { error = "卸载项目时发生错误", details = ex.Message });
            }
        }

        [HttpPost("export")]
        public async Task<IActionResult> ExportWav([FromBody] ExportWavRequest request) {
            try {
                if (currentProject == null) {
                    return BadRequest(new { error = "没有加载的项目" });
                }

                if (string.IsNullOrEmpty(request.OutputPath)) {
                    return BadRequest(new { error = "输出路径不能为空" });
                }

                // 确保输出目录存在
                var outputDir = Path.GetDirectoryName(request.OutputPath);
                if (!string.IsNullOrEmpty(outputDir)) {
                    try {
                        if (!Directory.Exists(outputDir)) {
                            Directory.CreateDirectory(outputDir);
                        }
                        // 检查目录权限
                        var testFile = Path.Combine(outputDir, "test.tmp");
                        System.IO.File.WriteAllText(testFile, "test");
                        System.IO.File.Delete(testFile);
                    }
                    catch (UnauthorizedAccessException) {
                        return StatusCode(403, new { error = "没有权限访问输出目录", path = outputDir });
                    }
                    catch (Exception ex) {
                        return StatusCode(500, new { error = "创建输出目录失败", details = ex.Message });
                    }
                }

                // 执行导出
                var cancellation = new CancellationTokenSource();
                try {
                    RenderEngine engine = new RenderEngine(currentProject);
                    var projectMix = engine.RenderMixdown(DocManager.Inst.MainScheduler, ref cancellation, wait: true).Item1;
                    DocManager.Inst.ExecuteCmd(new ProgressBarNotification(0, $"Exporting to {request.OutputPath}."));

                    // 确保目标文件可写
                    if (System.IO.File.Exists(request.OutputPath)) {
                        try {
                            using (FileStream fs = System.IO.File.Open(request.OutputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                                fs.Close();
                            }
                        }
                        catch (IOException) {
                            return StatusCode(409, new { error = "输出文件被其他程序占用", path = request.OutputPath });
                        }
                    }

                    WaveFileWriter.CreateWaveFile16(request.OutputPath, new ExportAdapter(projectMix).ToMono(1, 0));
                    DocManager.Inst.ExecuteCmd(new ProgressBarNotification(0, $"Exported to {request.OutputPath}."));
                }
                catch (IOException ioe) {
                    var customEx = new MessageCustomizableException($"Failed to export {request.OutputPath}.", $"<translate:errors.failed.export>: {request.OutputPath}", ioe);
                    DocManager.Inst.ExecuteCmd(new ErrorMessageNotification(customEx));
                    DocManager.Inst.ExecuteCmd(new ProgressBarNotification(0, $"Failed to export {request.OutputPath}."));
                    return StatusCode(500, new { error = "导出文件失败", details = ioe.Message });
                }
                catch (Exception e) {
                    var customEx = new MessageCustomizableException("Failed to render.", $"<translate:errors.failed.render>: {request.OutputPath}", e);
                    DocManager.Inst.ExecuteCmd(new ErrorMessageNotification(customEx));
                    DocManager.Inst.ExecuteCmd(new ProgressBarNotification(0, $"Failed to render."));
                    return StatusCode(500, new { error = "渲染失败", details = e.Message });
                }

                return Ok(new {
                    status = "ok",
                    message = "导出成功",
                    outputPath = request.OutputPath
                });
            }
            catch (Exception ex) {
                Log.Error(ex, "导出WAV时发生错误");
                return StatusCode(500, new { error = "导出WAV时发生错误", details = ex.Message });
            }
        }

        [HttpPost("convert-midi")]
        public async Task<IActionResult> ConvertMidiToUstx(
            [FromForm] List<IFormFile> midiFiles,
            [FromForm] string[] singers,
            [FromForm] string[] phonemizers,
            [FromForm] double? bpm = null) {
            try {
                if (midiFiles == null || !midiFiles.Any()) {
                    return BadRequest(new { error = "未提供MIDI文件" });
                }

                if (singers == null || !singers.Any()) {
                    return BadRequest(new { error = "未提供歌手信息" });
                }

                if (phonemizers == null || !phonemizers.Any()) {
                    return BadRequest(new { error = "未提供音素器信息" });
                }

                // 创建临时目录存储MIDI文件
                string tempDir = Path.Combine(Path.GetTempPath(), "OpenUtau_MidiImport_" + Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempDir);

                try {
                    // 保存上传的MIDI文件
                    List<string> midiFilePaths = new List<string>();
                    foreach (var file in midiFiles) {
                        if (file.ContentType != "audio/midi" && file.ContentType != "audio/x-midi") {
                            return BadRequest(new { error = $"文件 {file.FileName} 不是有效的MIDI文件" });
                        }

                        string filePath = Path.Combine(tempDir, file.FileName);
                        using (var stream = new FileStream(filePath, FileMode.Create)) {
                            await file.CopyToAsync(stream);
                        }
                        midiFilePaths.Add(filePath);
                    }

                    // 创建新项目
                    var project = new UProject();
                    Format.Ustx.AddDefaultExpressions(project);

                    // 设置项目BPM
                    project.tempos.Clear();
                    if (bpm.HasValue) {
                        project.tempos.Add(new UTempo(0, bpm.Value));
                    } else {
                        // 如果没有提供BPM，尝试从MIDI文件中读取
                        var midiFile = Melanchall.DryWetMidi.Core.MidiFile.Read(midiFilePaths[0]);
                        var tempoMap = midiFile.GetTempoMap();
                        var tempos = tempoMap.GetTempoChanges();
                        
                        if (tempos.Any()) {
                            // 使用第一个tempo事件
                            var firstTempo = tempos.First();
                            double bpmValue = 60000000.0 / firstTempo.Value.MicrosecondsPerQuarterNote;
                            project.tempos.Add(new UTempo(0, bpmValue));
                            Log.Information($"从MIDI文件中读取BPM: {bpmValue}");
                        } else {
                            // 如果MIDI文件中没有BPM信息，尝试通过音符间隔推测
                            try {
                                var midiFile2 = Melanchall.DryWetMidi.Core.MidiFile.Read(midiFilePaths[0]);
                                var notes = midiFile2.GetNotes();
                                
                                if (notes.Any()) {
                                    // 计算相邻音符之间的时间间隔
                                    var intervals = new List<double>();
                                    var sortedNotes = notes.OrderBy(n => n.Time);
                                    var previousNote = sortedNotes.First();
                                    
                                    foreach (var note in sortedNotes.Skip(1)) {
                                        var interval = note.Time - previousNote.Time;
                                        if (interval > 0) {
                                            intervals.Add(interval);
                                        }
                                        previousNote = note;
                                    }
                                    
                                    if (intervals.Any()) {
                                        // 使用最常见的间隔来推测BPM
                                        var mostCommonInterval = intervals
                                            .GroupBy(i => Math.Round(i / 10.0) * 10) // 将间隔分组，允许10个tick的误差
                                            .OrderByDescending(g => g.Count())
                                            .First()
                                            .Key;
                                        
                                        // 将tick转换为BPM
                                        double ticksPerQuarterNote = midiFile2.TimeDivision is TicksPerQuarterNoteTimeDivision timeDivision
                                            ? timeDivision.TicksPerQuarterNote 
                                            : 480;
                                        double bpmValue = 60000000.0 / (mostCommonInterval * 1000000.0 / ticksPerQuarterNote);
                                        
                                        // 确保BPM在合理范围内
                                        bpmValue = Math.Max(30, Math.Min(300, bpmValue));
                                        
                                        project.tempos.Add(new UTempo(0, bpmValue));
                                        Log.Information($"通过音符间隔推测BPM: {bpmValue}");
                                    } else {
                                        project.tempos.Add(new UTempo(0, 120.0));
                                        Log.Warning("无法通过音符间隔推测BPM，使用默认值120.0");
                                    }
                                } else {
                                    project.tempos.Add(new UTempo(0, 120.0));
                                    Log.Warning("MIDI文件中没有音符，使用默认值120.0");
                                }
                            }
                            catch (Exception ex) {
                                Log.Warning(ex, "BPM推测失败，使用默认值120.0");
                                project.tempos.Add(new UTempo(0, 120.0));
                            }
                        }
                    }

                    // 导入MIDI文件
                    foreach (var midiPath in midiFilePaths) {
                        var parts = OpenUtau.Core.Format.MidiWriter.Load(midiPath, project);
                        int singerIndex = 0;
                        int phonemizerIndex = 0;
                        foreach (var part in parts) {
                            var track = new UTrack(project);
                            track.TrackNo = project.tracks.Count;
                            part.trackNo = track.TrackNo;
                            
                            // 设置歌手
                            if (singerIndex < singers.Length) {
                                track.Singer = SingerManager.Inst.GetSinger(singers[singerIndex]);
                                if (track.Singer == null) {
                                    track.Singer = USinger.CreateMissing(singers[singerIndex]);
                                }
                                singerIndex++;
                            }
                            
                            // 设置音素器
                            if (phonemizerIndex < phonemizers.Length) {
                                var phonemizerType = System.Type.GetType(phonemizers[phonemizerIndex]);
                                if (phonemizerType != null) {
                                    var factory = DocManager.Inst.PhonemizerFactories.FirstOrDefault(f => f.type == phonemizerType);
                                    if (factory != null) {
                                        track.Phonemizer = factory.Create();
                                    }
                                }
                                phonemizerIndex++;
                            }

                            part.AfterLoad(project, track);
                            project.tracks.Add(track);
                            project.parts.Add(part);
                        }
                    }

                    // 将项目转换为USTX格式
                    string tempUstxPath = Path.Combine(tempDir, "temp.ustx");
                    Format.Ustx.Save(tempUstxPath, project);
                    string ustxContent = await System.IO.File.ReadAllTextAsync(tempUstxPath);

                    return Ok(new {
                        status = "ok",
                        message = "MIDI文件转换成功",
                        ustxContent = ustxContent
                    });
                }
                finally {
                    // 清理临时文件
                    try {
                        ;
                        //Directory.Delete(tempDir, true);
                    }
                    catch (Exception ex) {
                        Log.Warning(ex, "清理临时文件失败");
                    }
                }
            }
            catch (Exception ex) {
                Log.Error(ex, "转换MIDI文件时发生错误");
                return StatusCode(500, new { error = "转换MIDI文件时发生错误", details = ex.Message });
            }
        }
    }

    public class LoadProjectRequest {
        public string FilePath { get; set; } = string.Empty;
        public string UstxContent { get; set; } = string.Empty;
    }

    public class ExportWavRequest {
        public string OutputPath { get; set; } = string.Empty;
    }
}
