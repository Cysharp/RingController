package com.cysharp.ringcontroller;

import android.content.Context;
import android.content.pm.PackageManager;
import android.graphics.drawable.Drawable;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.BaseAdapter;
import android.widget.ImageView;
import android.widget.TextView;

/**
 * Pure Java BaseAdapter for launcher app picker (CoreCLR: C# must not subclass BaseAdapter).
 * View IDs are passed from managed code so this file does not reference R (javac runs before R.java).
 */
public final class RingAppPickerAdapter extends BaseAdapter {
    private final Context context;
    private final PackageManager pm;
    private final String[] labels;
    private final String[] packages;
    private final int layoutResId;
    private final int iconViewId;
    private final int labelViewId;

    public RingAppPickerAdapter(Context context, PackageManager pm, String[] labels, String[] packages,
            int layoutResId, int iconViewId, int labelViewId) {
        this.context = context;
        this.pm = pm;
        this.labels = labels;
        this.packages = packages;
        this.layoutResId = layoutResId;
        this.iconViewId = iconViewId;
        this.labelViewId = labelViewId;
    }

    @Override
    public int getCount() {
        return labels.length;
    }

    @Override
    public Object getItem(int position) {
        return packages[position];
    }

    @Override
    public long getItemId(int position) {
        return position;
    }

    @Override
    public View getView(int position, View convertView, ViewGroup parent) {
        View row = convertView;
        if (row == null) {
            row = LayoutInflater.from(context).inflate(layoutResId, parent, false);
        }
        ImageView iconView = row.findViewById(iconViewId);
        TextView labelView = row.findViewById(labelViewId);
        String label = labels[position];
        String pkg = packages[position];
        labelView.setText(label);
        Drawable d = null;
        try {
            d = pm.getApplicationIcon(pkg);
        } catch (Exception ignored) {
        }
        iconView.setImageDrawable(d);
        return row;
    }
}
