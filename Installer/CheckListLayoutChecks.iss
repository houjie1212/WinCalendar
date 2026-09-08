// 仅通过编译开关启用：初始化阶段断言后退出，绝不进入安装或卸载。
procedure LayoutAssert(Condition: Boolean);
begin
  if not Condition then RaiseException('Checklist layout assertion failed');
end;

procedure RunLayoutChecks();
var
  Page: TWizardPage;
  Panel: TPanel;
  List, Larger: TNewCheckListBox;
  KeepBox: TNewCheckBox;
  I, OriginalHeight: Integer;
begin
  LayoutAssert(WizardForm.TasksList.Offset >= ScaleX(12));
  LayoutAssert(WizardForm.RunList.Offset >= ScaleX(12));
  LayoutAssert(WizardForm.TasksList.MinItemHeight >= ScaleY(24));
  LayoutAssert(WizardForm.RunList.MinItemHeight >= ScaleY(24));
  Page := CreateCustomPage(wpWelcome, 'Layout checks', 'Dynamic nested controls');
  Panel := TPanel.Create(Page);
  Panel.Parent := Page.Surface;
  List := TNewCheckListBox.Create(Page);
  List.Parent := Panel;
  List.Offset := 0;
  List.MinItemHeight := 0;
  List.AddCheckBox('unchecked', '', 0, False, True, False, True, nil);
  List.AddCheckBox('checked', '', 0, True, True, False, True, nil);
  List.AddCheckBox('disabled', '', 0, False, False, False, True, nil);
  Larger := TNewCheckListBox.Create(Page);
  Larger.Parent := Panel;
  Larger.Offset := ScaleX(20);
  Larger.MinItemHeight := ScaleY(36);
  KeepBox := TNewCheckBox.Create(Page);
  KeepBox.Parent := Panel;
  KeepBox.Height := ScaleY(24);
  KeepBox.Checked := False;
  OriginalHeight := KeepBox.Height;
  for I := 0 to 9 do ApplyCheckListLayout(WizardForm);
  LayoutAssert((List.Offset = ScaleX(12)) and (List.MinItemHeight = ScaleY(24)));
  LayoutAssert((Larger.Offset = ScaleX(20)) and (Larger.MinItemHeight = ScaleY(36)));
  LayoutAssert(not List.Checked[0] and List.Checked[1] and not List.Checked[2]);
  LayoutAssert(not KeepBox.Checked and (KeepBox.Height = OriginalHeight));
  LayoutAssert(SaveStringToFile(ExpandConstant('{param:CHECKRESULT}'), 'PASS', False));
  Abort;
end;
