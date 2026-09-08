// 统一处理所有复选列表，保留更大的页面自定义值，不改变原生绘制或选择状态。
procedure ApplyCheckListLayout(Parent: TWinControl);
var
  I: Integer;
  Child: TControl;
  List: TNewCheckListBox;
begin
  for I := 0 to Parent.ControlCount - 1 do begin
    Child := Parent.Controls[I];
    if Child is TNewCheckListBox then begin
      List := TNewCheckListBox(Child);
      if List.Offset < ScaleX(12) then List.Offset := ScaleX(12);
      if List.MinItemHeight < ScaleY(24) then List.MinItemHeight := ScaleY(24);
    end;
    if Child is TWinControl then ApplyCheckListLayout(TWinControl(Child));
  end;
end;
