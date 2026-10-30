using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenCloud.Assets.Models;

namespace OpenCloud.Gameplay;

/// <summary>Flags of a motion request, as the character's motion driver reads them.</summary>
[Flags]
public enum MotionFlags
{
    None = 0,

    /// <summary>The frame does not advance.</summary>
    Pause = 1,

    /// <summary>The motion stops on its last frame instead of looping.</summary>
    Hold = 2,

    /// <summary>The motion starts again from its first frame even if it is already playing.</summary>
    Restart = 4,
}

/// <summary>
/// Plays a character's motions by their game names (the <c>KEY</c> names of its
/// <c>info.cfg</c>, such as 立ち or 走り), the way <c>CCharacter2::SetMotion</c>,
/// <c>SetStep</c> and <c>NormalDrive</c> drive them: a request takes effect on the next game
/// frame, a motion already playing keeps its frame, a new one blends in, and the step is the
/// number of motion frames advanced per game frame (reset to the <c>KEY</c> step on change).
/// </summary>
public sealed class CharacterMotion
{
    /// <summary>Blend weight a new motion starts at, and what each game frame adds to it.</summary>
    private const float BlendStart = 0.1f;
    private const float BlendSpeed = 0.2f;

    private readonly AnimationPlayer? _player;
    private readonly Dictionary<string, string> _clips = new(StringComparer.Ordinal);
    private string? _requested;
    private MotionFlags _requestedFlags;
    private float _step;
    private bool _held;

    public CharacterMotion(AnimationPlayer? player)
    {
        _player = player;
        if (player == null)
            return;
        // The first KEY of a name wins, searching the slots in order and each slot in KEY order;
        // Godot lists libraries and clips by name, so both orders are rebuilt here.
        var libraries = player.GetAnimationLibraryList()
            .OrderBy(l => int.TryParse(l.ToString().TrimStart('m'), out int slot) ? slot : int.MaxValue);
        foreach (var library in libraries)
        {
            var animations = player.GetAnimationLibrary(library);
            var clips = animations.GetAnimationList()
                .Select(clip => (Name: clip.ToString(), Animation: animations.GetAnimation(clip)))
                .OrderBy(c => c.Animation.HasMeta(MdsModelLoader.KeyIndexMeta) ? (int)c.Animation.GetMeta(MdsModelLoader.KeyIndexMeta) : int.MaxValue);
            foreach (var (clip, animation) in clips)
            {
                string game = animation.HasMeta("game_name") ? (string)animation.GetMeta("game_name") : clip;
                _clips.TryAdd(game, library.IsEmpty ? clip : $"{library}/{clip}");
            }
        }
    }

    /// <summary>The motion now playing, by game name, or null.</summary>
    public string? Current { get; private set; }

    public MotionFlags CurrentFlags { get; private set; }

    /// <summary>Motion frames advanced per game frame.</summary>
    public float Step => _step;

    public bool Has(string name) => _clips.ContainsKey(name);

    /// <summary>Asks for a motion; unknown names are ignored, as the game ignores them.</summary>
    public void SetMotion(string name, MotionFlags flags = MotionFlags.None)
    {
        if (!_clips.ContainsKey(name))
            return;
        _requested = name;
        _requestedFlags = flags;
        Apply();
    }

    public void SetStep(float step)
    {
        _step = step;
        ApplySpeed();
    }

    /// <summary>The step of the current motion's <c>KEY</c>.</summary>
    public float DefaultStep => Current != null && Clip(Current) is { } animation && animation.HasMeta("step")
        ? (float)animation.GetMeta("step")
        : 1.0f;

    /// <summary>True when no motion plays or a held motion reached its last frame.</summary>
    public bool IsEnded => Current == null || _held;

    /// <summary>Called once per game frame after the controller ran: stops held motions at their end.</summary>
    public void Tick()
    {
        if (_player == null || Current == null || (CurrentFlags & MotionFlags.Hold) == 0 || _held)
            return;
        double next = _player.CurrentAnimationPosition + _step / MotionAnimationBuilder.FramesPerSecond;
        if (next >= _player.CurrentAnimationLength)
        {
            _player.Seek(_player.CurrentAnimationLength, true);
            _held = true;
            ApplySpeed();
        }
    }

    private void Apply()
    {
        if (_player == null || _requested == null)
            return;
        bool change = _requested != Current;
        bool restart = (_requestedFlags & MotionFlags.Restart) != 0 && (change || _requestedFlags != CurrentFlags);
        if (!change && !restart && _requestedFlags == CurrentFlags)
            return;

        CurrentFlags = _requestedFlags;
        if (change || restart)
        {
            string clip = _clips[_requested];
            // Blend weight goes from 0.1 by 0.2 a game frame: fully in after 4.5 frames.
            double blend = Current == null ? 0.0 : (1.0f - BlendStart) / BlendSpeed / MotionAnimationBuilder.FramesPerSecond;
            _player.Play(clip, blend);
            if (restart || change)
                _player.Seek(0, true);
            Current = _requested;
            _held = false;
            if (change)
                _step = DefaultStep;
        }
        ApplySpeed();
    }

    private void ApplySpeed()
    {
        if (_player == null)
            return;
        bool paused = (CurrentFlags & MotionFlags.Pause) != 0 || _held;
        _player.SpeedScale = paused ? 0.0f : Math.Max(_step, 0.0f);
    }

    private Animation? Clip(string name) =>
        _player != null && _clips.TryGetValue(name, out var clip) && _player.HasAnimation(clip) ? _player.GetAnimation(clip) : null;
}
